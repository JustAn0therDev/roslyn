﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

// #define LOG

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

#if LOG
using System.IO;
using System.Text.RegularExpressions;
#endif

namespace Microsoft.CodeAnalysis.SimplifyTypeNames
{
    internal abstract class SimplifyTypeNamesDiagnosticAnalyzerBase<TLanguageKindEnum> : DiagnosticAnalyzer, IBuiltInAnalyzer where TLanguageKindEnum : struct
    {
#if LOG
        private static string _logFile = @"c:\temp\simplifytypenames.txt";
        private static object _logGate = new object();
        private static readonly Regex s_newlinePattern = new Regex(@"[\r\n]+");
#endif

        private static readonly LocalizableString s_localizableMessage = new LocalizableResourceString(nameof(WorkspacesResources.Name_can_be_simplified), WorkspacesResources.ResourceManager, typeof(WorkspacesResources));

        private static readonly LocalizableString s_localizableTitleSimplifyNames = new LocalizableResourceString(nameof(FeaturesResources.Simplify_Names), FeaturesResources.ResourceManager, typeof(FeaturesResources));
        private static readonly DiagnosticDescriptor s_descriptorSimplifyNames = new DiagnosticDescriptor(IDEDiagnosticIds.SimplifyNamesDiagnosticId,
                                                                    s_localizableTitleSimplifyNames,
                                                                    s_localizableMessage,
                                                                    DiagnosticCategory.Style,
                                                                    DiagnosticSeverity.Hidden,
                                                                    isEnabledByDefault: true,
                                                                    customTags: DiagnosticCustomTags.Unnecessary);

        private static readonly LocalizableString s_localizableTitleSimplifyMemberAccess = new LocalizableResourceString(nameof(FeaturesResources.Simplify_Member_Access), FeaturesResources.ResourceManager, typeof(FeaturesResources));
        private static readonly DiagnosticDescriptor s_descriptorSimplifyMemberAccess = new DiagnosticDescriptor(IDEDiagnosticIds.SimplifyMemberAccessDiagnosticId,
                                                                    s_localizableTitleSimplifyMemberAccess,
                                                                    s_localizableMessage,
                                                                    DiagnosticCategory.Style,
                                                                    DiagnosticSeverity.Hidden,
                                                                    isEnabledByDefault: true,
                                                                    customTags: DiagnosticCustomTags.Unnecessary);

        private static readonly DiagnosticDescriptor s_descriptorPreferBuiltinOrFrameworkType = new DiagnosticDescriptor(IDEDiagnosticIds.PreferBuiltInOrFrameworkTypeDiagnosticId,
            s_localizableTitleSimplifyNames,
            s_localizableMessage,
            DiagnosticCategory.Style,
            DiagnosticSeverity.Hidden,
            isEnabledByDefault: true,
            customTags: DiagnosticCustomTags.Unnecessary);

        internal abstract bool IsCandidate(SyntaxNode node);
        internal abstract bool CanSimplifyTypeNameExpression(
            SemanticModel model, SyntaxNode node, OptionSet optionSet,
            out TextSpan issueSpan, out string diagnosticId, out bool inDeclaration,
            CancellationToken cancellationToken);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
            = ImmutableArray.Create(
                    s_descriptorSimplifyNames,
                    s_descriptorSimplifyMemberAccess,
                    s_descriptorPreferBuiltinOrFrameworkType);

        protected SimplifyTypeNamesDiagnosticAnalyzerBase()
        {
        }

        public bool OpenFileOnly(OptionSet options)
        {
            var preferTypeKeywordInDeclarationOption = options.GetOption(
                CodeStyleOptions.PreferIntrinsicPredefinedTypeKeywordInDeclaration, GetLanguageName())!.Notification;
            var preferTypeKeywordInMemberAccessOption = options.GetOption(
                CodeStyleOptions.PreferIntrinsicPredefinedTypeKeywordInMemberAccess, GetLanguageName())!.Notification;

            return !(preferTypeKeywordInDeclarationOption == NotificationOption.Warning || preferTypeKeywordInDeclarationOption == NotificationOption.Error ||
                     preferTypeKeywordInMemberAccessOption == NotificationOption.Warning || preferTypeKeywordInMemberAccessOption == NotificationOption.Error);
        }

        public sealed override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(AnalyzeCompilation);
        }

        private void AnalyzeCompilation(CompilationStartAnalysisContext context)
        {
            var analyzer = new AnalyzerImpl(this);
            context.RegisterCodeBlockAction(analyzer.AnalyzeCodeBlock);
            context.RegisterSemanticModelAction(analyzer.AnalyzeSemanticModel);
        }

        protected abstract void AnalyzeCodeBlock(CodeBlockAnalysisContext context);
        protected abstract void AnalyzeSemanticModel(SemanticModelAnalysisContext context, SimpleIntervalTree<TextSpan, TextSpanIntervalIntrospector>? codeBlockIntervalTree);

        protected abstract string GetLanguageName();

        public bool TrySimplify(SemanticModel model, SyntaxNode node, [NotNullWhen(true)] out Diagnostic? diagnostic, OptionSet optionSet, CancellationToken cancellationToken)
        {
            if (!CanSimplifyTypeNameExpression(
                    model, node, optionSet,
                    out var issueSpan, out var diagnosticId, out var inDeclaration,
                    cancellationToken))
            {
                diagnostic = null;
                return false;
            }

            if (model.SyntaxTree.OverlapsHiddenPosition(issueSpan, cancellationToken))
            {
                diagnostic = null;
                return false;
            }

            diagnostic = CreateDiagnostic(model, optionSet, issueSpan, diagnosticId, inDeclaration);
            return true;
        }

        internal static Diagnostic CreateDiagnostic(SemanticModel model, OptionSet optionSet, TextSpan issueSpan, string diagnosticId, bool inDeclaration)
        {
            PerLanguageOption<CodeStyleOption<bool>> option;
            DiagnosticDescriptor descriptor;
            ReportDiagnostic severity;
            switch (diagnosticId)
            {
                case IDEDiagnosticIds.SimplifyNamesDiagnosticId:
                    descriptor = s_descriptorSimplifyNames;
                    severity = descriptor.DefaultSeverity.ToReportDiagnostic();
                    break;

                case IDEDiagnosticIds.SimplifyMemberAccessDiagnosticId:
                    descriptor = s_descriptorSimplifyMemberAccess;
                    severity = descriptor.DefaultSeverity.ToReportDiagnostic();
                    break;

                case IDEDiagnosticIds.PreferBuiltInOrFrameworkTypeDiagnosticId:
                    option = inDeclaration
                        ? CodeStyleOptions.PreferIntrinsicPredefinedTypeKeywordInDeclaration
                        : CodeStyleOptions.PreferIntrinsicPredefinedTypeKeywordInMemberAccess;
                    descriptor = s_descriptorPreferBuiltinOrFrameworkType;

                    var optionValue = optionSet.GetOption(option, model.Language)!;
                    severity = optionValue.Notification.Severity;
                    break;
                default:
                    throw ExceptionUtilities.UnexpectedValue(diagnosticId);
            }

            var tree = model.SyntaxTree;
            var builder = ImmutableDictionary.CreateBuilder<string, string>();
            builder["OptionName"] = nameof(CodeStyleOptions.PreferIntrinsicPredefinedTypeKeywordInMemberAccess); // TODO: need the actual one
            builder["OptionLanguage"] = model.Language;
            var diagnostic = DiagnosticHelper.Create(descriptor, tree.GetLocation(issueSpan), severity, additionalLocations: null, builder.ToImmutable());

#if LOG
            var sourceText = tree.GetText();
            sourceText.GetLineAndOffset(issueSpan.Start, out var startLineNumber, out var startOffset);
            sourceText.GetLineAndOffset(issueSpan.End, out var endLineNumber, out var endOffset);
            var logLine = tree.FilePath + "," + startLineNumber + "\t" + diagnosticId + "\t" + inDeclaration + "\t";

            var leading = sourceText.ToString(TextSpan.FromBounds(
                sourceText.Lines[startLineNumber].Start, issueSpan.Start));
            var mid = sourceText.ToString(issueSpan);
            var trailing = sourceText.ToString(TextSpan.FromBounds(
                issueSpan.End, sourceText.Lines[endLineNumber].End));

            var contents = leading + "[|" + s_newlinePattern.Replace(mid, " ") + "|]" + trailing;
            logLine += contents + "\r\n";

            lock (_logGate)
            {
                File.AppendAllText(_logFile, logLine);
            }
#endif

            return diagnostic;
        }

        public DiagnosticAnalyzerCategory GetAnalyzerCategory()
            => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;

        private class AnalyzerImpl
        {
            private readonly SimplifyTypeNamesDiagnosticAnalyzerBase<TLanguageKindEnum> _analyzer;
            private readonly ConcurrentDictionary<SyntaxTree, (StrongBox<bool> completed, SimpleIntervalTree<TextSpan, TextSpanIntervalIntrospector>? intervalTree)> _codeBlockIntervals = new ConcurrentDictionary<SyntaxTree, (StrongBox<bool> completed, SimpleIntervalTree<TextSpan, TextSpanIntervalIntrospector>? intervalTree)>();

            public AnalyzerImpl(SimplifyTypeNamesDiagnosticAnalyzerBase<TLanguageKindEnum> analyzer)
            {
                _analyzer = analyzer;
            }

            public void AnalyzeCodeBlock(CodeBlockAnalysisContext context)
            {
                if (_analyzer.IsIgnoredCodeBlock(ref context))
                    return;

                var (completed, intervalTree) = _codeBlockIntervals.GetOrAdd(context.CodeBlock.SyntaxTree, _ => (new StrongBox<bool>(false), SimpleIntervalTree.Create(new TextSpanIntervalIntrospector(), Array.Empty<TextSpan>())));
                if (completed.Value)
                    return;

                RoslynDebug.AssertNotNull(intervalTree);
                lock (completed)
                {
                    if (completed.Value)
                        return;

                    if (intervalTree.HasIntervalThatOverlapsWith(context.CodeBlock.FullSpan.Start, context.CodeBlock.FullSpan.End))
                        return;

                    intervalTree.AddIntervalInPlace(context.CodeBlock.FullSpan);
                }

                _analyzer.AnalyzeCodeBlock(context);
            }

            public void AnalyzeSemanticModel(SemanticModelAnalysisContext context)
            {
                var (completed, intervalTree) = _codeBlockIntervals.GetOrAdd(context.SemanticModel.SyntaxTree, syntaxTree => (new StrongBox<bool>(true), null));
                if (!completed.Value)
                {
                    lock (completed)
                    {
                        // Prevent future code block callbacks from analyzing more spans within this tree
                        completed.Value = true;
                    }
                }

                _analyzer.AnalyzeSemanticModel(context, intervalTree);
            }
        }
    }
}
