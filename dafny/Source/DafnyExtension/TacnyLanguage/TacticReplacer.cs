﻿using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace DafnyLanguage.TacnyLanguage
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("dafny")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal class TacticReplacerFilterProvider : IVsTextViewCreationListener
    {
        [Import(typeof(IVsEditorAdaptersFactoryService))]
        internal IVsEditorAdaptersFactoryService EditorAdaptersFactory { get; set; }

        [Import(typeof(ITextStructureNavigatorSelectorService))]
        internal ITextStructureNavigatorSelectorService TextStructureNavigatorSelector { get; set; }

        [Import(typeof(SVsServiceProvider))]
        internal System.IServiceProvider ServiceProvider { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            IWpfTextView textView = EditorAdaptersFactory.GetWpfTextView(textViewAdapter);
            if (textView == null) return;
            ITextStructureNavigator navigator = TextStructureNavigatorSelector.GetTextStructureNavigator(textView.TextBuffer);
            AddCommandFilter(textViewAdapter, new TacticReplacerCommandFilter(textView, navigator, ServiceProvider));
        }

        void AddCommandFilter(IVsTextView viewAdapter, TacticReplacerCommandFilter commandFilter)
        {
            IOleCommandTarget next;
            if (VSConstants.S_OK == viewAdapter.AddCommandFilter(commandFilter, out next))
                if (next != null)
                    commandFilter.NextCmdTarget = next;
        }
    }

    internal class TacticReplacerCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _tv;
        private readonly ITextStructureNavigator _tsn;
        private readonly System.IServiceProvider _isp;
        internal IOleCommandTarget NextCmdTarget;

        public TacticReplacerCommandFilter(IWpfTextView textView, ITextStructureNavigator tsn, System.IServiceProvider sp)
        {
            _tv = textView;
            _tsn = tsn;
            _isp = sp;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdId, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            var status = TacticReplaceStatus.NoTactic;

            if (VsShellUtilities.IsInAutomationFunction(_isp))
                return NextCmdTarget.Exec(pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
            
            if (pguidCmdGroup == VSConstants.VSStd2K && nCmdId == (uint)VSConstants.VSStd2KCmdID.TYPECHAR/*&& Keyboard.Modifiers==ModifierKeys.Alt */)
            {
                var typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                if (typedChar.Equals('#'))
                {
                        status = ExecuteReplacement();
                }
            }
            
            return status==TacticReplaceStatus.NoTactic ? NextCmdTarget.Exec(pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut) : VSConstants.S_OK; 
        }

        internal enum TacticReplaceStatus { Success, NoTactic }

        private TacticReplaceStatus ExecuteReplacement()
        {
            var startingWordPosition = _tv.GetTextElementSpan(_tv.Caret.Position.BufferPosition);
            //probably want to check were not in a comment block

            var startingWordPoint = _tv.Caret.Position.Point.GetPoint(_tv.TextBuffer, PositionAffinity.Predecessor);
            if(startingWordPoint==null)return TacticReplaceStatus.NoTactic;
            var startingWord = _tv.TextSnapshot.GetText(_tsn.GetExtentOfWord(startingWordPoint.Value).Span);

            var startOfBlock = StartOfBlock(startingWordPosition);
            if (startOfBlock == -1) return TacticReplaceStatus.NoTactic;

            var lengthOfBlock = LengthOfBlock(startOfBlock);
            
            var newSpan = DafnyDriver.GetExpandedTactic(startingWord);

            var tedit = _tv.TextBuffer.CreateEdit();
            tedit.Replace(startOfBlock, lengthOfBlock, newSpan);
            tedit.Apply();
            return TacticReplaceStatus.Success;
        }

        private int LengthOfBlock(int startingPoint)
        {
            var advancingPoint = startingPoint;
            var bodyOpened = false;
            var braceDepth = 0;
            while (braceDepth > 0 || !bodyOpened)
            {
                var advancingSnapshot = new SnapshotPoint(_tv.TextSnapshot, ++advancingPoint);
                var currentChar = advancingSnapshot.GetChar();
                if (currentChar == '{')
                {
                    braceDepth++;
                    bodyOpened = true;
                }
                else if (currentChar == '}')
                {
                    braceDepth--;
                }
            }
            return advancingPoint - startingPoint + 1;
        }

        private int StartOfBlock(SnapshotSpan startingWordPosition)
        {
            var listOfStarters = new[] {
                "function",
                "lemma",
                "method",
                "predicate"
            };
            
            var currentSubling = _tsn.GetSpanOfNextSibling(startingWordPosition);

            //var xnext = currentSubling;
            //var xprev = currentSubling;
            //var x = 0;
            //while (x-->0)
            //{
            //    xnext = _tsn.GetSpanOfNextSibling(xnext); //This doesnt like line breaks. Or periods.
            //    xprev = _tsn.GetSpanOfPreviousSibling(xprev);
            //}
            
            var next = _tsn.GetSpanOfNextSibling(currentSubling);
            if (next.GetText() != "(") return -1;

            var prev = _tsn.GetSpanOfPreviousSibling(currentSubling);
            var s = prev.GetText();
            if (!listOfStarters.Contains(s)) return -1;

            var startingPos = prev.Start;
            var ghostPrev = _tsn.GetSpanOfPreviousSibling(prev);
            if (ghostPrev.GetText() == "ghost") return ghostPrev.Start;
            return startingPos;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return NextCmdTarget.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }
    }
}