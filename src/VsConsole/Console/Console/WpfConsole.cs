using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;

using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

using EditorDefGuidList = Microsoft.VisualStudio.Editor.DefGuidList;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using IServiceProvider = System.IServiceProvider;

namespace NuGetConsole.Implementation.Console {
    internal interface IPrivateWpfConsole : IWpfConsole {
        void BeginInputLine();
        SnapshotPoint? InputLineStart { get; }
        SnapshotSpan? EndInputLine(bool isEcho);
        InputHistory InputHistory { get; }

    }

    internal class WpfConsole : ObjectWithFactory<WpfConsoleService>, IDisposable {
        private IServiceProvider ServiceProvider { get; set; }
        public string ContentTypeName { get; private set; }
        public string HostName { get; private set; }
        private IVsStatusbar _vsStatusBar;
        private uint _pdwCookieForStatusBar;
        private readonly IPrivateConsoleStatus _consoleStatus;

        public event EventHandler<EventArgs<Tuple<SnapshotSpan, Color?, Color?>>> NewColorSpan;
        public event EventHandler ConsoleCleared;

        public WpfConsole(
            WpfConsoleService factory,
            IServiceProvider sp,
            IPrivateConsoleStatus consoleStatus,
            string contentTypeName,
            string hostName)
            : base(factory) {
            UtilityMethods.ThrowIfArgumentNull(sp);

            _consoleStatus = consoleStatus;
            ServiceProvider = sp;
            ContentTypeName = contentTypeName;
            HostName = hostName;
        }

        private IPrivateConsoleDispatcher _dispatcher;
        public IPrivateConsoleDispatcher Dispatcher {
            get {
                if (_dispatcher == null) {
                    _dispatcher = new ConsoleDispatcher(Marshaler);
                }
                return _dispatcher;
            }
        }

        public IVsUIShell VsUIShell {
            get {
                return ServiceProvider.GetService<IVsUIShell>(typeof(SVsUIShell));
            }
        }

        private IVsStatusbar VsStatusBar {
            get {
                if (_vsStatusBar == null) {
                    _vsStatusBar = ServiceProvider.GetService<IVsStatusbar>(typeof(SVsStatusbar));
                }
                return _vsStatusBar;
            }
        }

        private IOleServiceProvider OleServiceProvider {
            get {
                return ServiceProvider.GetService<IOleServiceProvider>(typeof(IOleServiceProvider));
            }
        }

        private IContentType _contentType;
        private IContentType ContentType {
            get {
                if (_contentType == null) {
                    _contentType = Factory.ContentTypeRegistryService.GetContentType(this.ContentTypeName);
                    if (_contentType == null) {
                        _contentType = Factory.ContentTypeRegistryService.AddContentType(
                            this.ContentTypeName, new string[] { "text" });
                    }
                }

                return _contentType;
            }
        }

        private IVsTextBuffer _bufferAdapter;
        private IVsTextBuffer VsTextBuffer {
            get {
                if (_bufferAdapter == null) {
                    _bufferAdapter = Factory.VsEditorAdaptersFactoryService.CreateVsTextBufferAdapter(OleServiceProvider, ContentType);
                    _bufferAdapter.InitializeContent(string.Empty, 0);
                }

                return _bufferAdapter;
            }
        }

        private IWpfTextView _wpfTextView;
        public IWpfTextView WpfTextView {
            get {
                if (_wpfTextView == null) {
                    _wpfTextView = Factory.VsEditorAdaptersFactoryService.GetWpfTextView(VsTextView);
                }

                return _wpfTextView;
            }
        }

        private IWpfTextViewHost WpfTextViewHost {
            get {
                IVsUserData userData = VsTextView as IVsUserData;
                object data;
                Guid guidIWpfTextViewHost = Microsoft.VisualStudio.Editor.DefGuidList.guidIWpfTextViewHost;
                userData.GetData(ref guidIWpfTextViewHost, out data);
                IWpfTextViewHost wpfTextViewHost = data as IWpfTextViewHost;

                return wpfTextViewHost;
            }
        }

        private enum ReadOnlyRegionType {
            /// <summary>
            /// No ReadOnly region. The whole text buffer allows edit.
            /// </summary>
            None,

            /// <summary>
            /// Begin and body are ReadOnly. Only allows edit at the end.
            /// </summary>
            BeginAndBody,

            /// <summary>
            /// The whole text buffer is ReadOnly. Does not allow any edit.
            /// </summary>
            All
        };

        private IReadOnlyRegion _readOnlyRegionBegin;
        private IReadOnlyRegion _readOnlyRegionBody;

        private void SetReadOnlyRegionType(ReadOnlyRegionType value) {
            ITextBuffer buffer = WpfTextView.TextBuffer;
            ITextSnapshot snapshot = buffer.CurrentSnapshot;

            using (IReadOnlyRegionEdit edit = buffer.CreateReadOnlyRegionEdit()) {
                edit.ClearReadOnlyRegion(ref _readOnlyRegionBegin);
                edit.ClearReadOnlyRegion(ref _readOnlyRegionBody);

                switch (value) {
                    case ReadOnlyRegionType.BeginAndBody:
                        if (snapshot.Length > 0) {
                            _readOnlyRegionBegin = edit.CreateReadOnlyRegion(new Span(0, 0), SpanTrackingMode.EdgeExclusive, EdgeInsertionMode.Deny);
                            _readOnlyRegionBody = edit.CreateReadOnlyRegion(new Span(0, snapshot.Length));
                        }
                        break;

                    case ReadOnlyRegionType.All:
                        _readOnlyRegionBody = edit.CreateReadOnlyRegion(new Span(0, snapshot.Length), SpanTrackingMode.EdgeExclusive, EdgeInsertionMode.Deny);
                        break;
                }

                edit.Apply();
            }
        }

        SnapshotPoint? _inputLineStart;

        /// <summary>
        /// Get current input line start point (updated to current WpfTextView's text snapshot).
        /// </summary>
        public SnapshotPoint? InputLineStart {
            get {
                if (_inputLineStart != null) {
                    ITextSnapshot snapshot = WpfTextView.TextSnapshot;
                    if (_inputLineStart.Value.Snapshot != snapshot) {
                        _inputLineStart = _inputLineStart.Value.TranslateTo(snapshot, PointTrackingMode.Negative);
                    }
                }
                return _inputLineStart;
            }
        }

        public SnapshotSpan GetInputLineExtent(int start = 0, int length = -1) {
            Debug.Assert(_inputLineStart != null);

            SnapshotPoint beginPoint = InputLineStart.Value + start;
            return length >= 0 ?
                new SnapshotSpan(beginPoint, length) :
                new SnapshotSpan(beginPoint, beginPoint.GetContainingLine().End);
        }

        public SnapshotSpan InputLineExtent {
            get {
                return GetInputLineExtent();
            }
        }

        /// <summary>
        /// Get the snapshot extent from InputLineStart to END. Normally this console expects
        /// one line only on InputLine. However in some cases multiple lines could appear, e.g.
        /// when a DTE event handler writes to the console. This scenario is not fully supported,
        /// but it is better to clean up nicely with ESC/ArrowUp/Return.
        /// </summary>
        public SnapshotSpan AllInputExtent {
            get {
                SnapshotPoint start = InputLineStart.Value;
                return new SnapshotSpan(start, start.Snapshot.GetEnd());
            }
        }

        public string InputLineText {
            get {
                return InputLineExtent.GetText();
            }
        }

        public void BeginInputLine() {
            if (_inputLineStart == null) {
                SetReadOnlyRegionType(ReadOnlyRegionType.BeginAndBody);
                _inputLineStart = WpfTextView.TextSnapshot.GetEnd();
            }
        }

        public SnapshotSpan? EndInputLine(bool isEcho = false) {
            // Reset history navigation upon end of a command line
            ResetNavigateHistory();

            if (_inputLineStart != null) {
                SnapshotSpan inputSpan = InputLineExtent;

                _inputLineStart = null;
                SetReadOnlyRegionType(ReadOnlyRegionType.All);
                if (!isEcho) {
                    Dispatcher.PostInputLine(new InputLine(inputSpan));
                }

                return inputSpan;
            }

            return null;
        }

        private PrivateMarshaler _marshaler;
        private PrivateMarshaler Marshaler {
            get {
                if (_marshaler == null) {
                    _marshaler = new PrivateMarshaler(this);
                }
                return _marshaler;
            }
        }

        public IWpfConsole MarshaledConsole {
            get { return this.Marshaler; }
        }

        class PrivateMarshaler : Marshaler<WpfConsole>, IWpfConsole, IPrivateWpfConsole {
            public PrivateMarshaler(WpfConsole impl)
                : base(impl) {
            }

            public IHost Host {
                get { return Invoke(() => _impl.Host); }
                set { Invoke(() => { _impl.Host = value; }); }
            }

            public IConsoleDispatcher Dispatcher {
                get { return Invoke(() => _impl.Dispatcher); }
            }

            public int ConsoleWidth {
                get { return Invoke(() => _impl.ConsoleWidth); }
            }

            public void Write(string text) {
                Invoke(() => _impl.Write(text));
            }

            public void WriteLine(string text) {
                Invoke(() => _impl.WriteLine(text));
            }

            public void WriteBackspace() {
                Invoke(() => _impl.WriteBackspace());
            }

            public void Write(string text, Color? foreground, Color? background) {
                Invoke(() => _impl.Write(text, foreground, background));
            }

            public void Clear() {
                Invoke(_impl.Clear);
            }

            public void SetExecutionMode(bool isExecuting) {
                Invoke(() => _impl.SetExecutionMode(isExecuting));
            }

            public object Content {
                get { return Invoke(() => _impl.Content); }
            }

            public void WriteProgress(string operation, int percentComplete) {
                Invoke(() => _impl.WriteProgress(operation, percentComplete));
            }

            public object VsTextView {
                get { return Invoke(() => _impl.VsTextView); }
            }

            public SnapshotPoint? InputLineStart {
                get { return Invoke(() => _impl.InputLineStart); }
            }

            public void BeginInputLine() {
                Invoke(() => _impl.BeginInputLine());
            }

            public SnapshotSpan? EndInputLine(bool isEcho) {
                return Invoke(() => _impl.EndInputLine(isEcho));
            }

            public InputHistory InputHistory {
                get { return Invoke(() => _impl.InputHistory); }
            }

            public bool ShowDisclaimerHeader {
                get {
                    return true;
                }
            }
        }

        private IHost _host;
        public IHost Host {
            get {
                return _host;
            }
            set {
                if (_host != null) {
                    throw new InvalidOperationException();
                }
                _host = value;
            }
        }

        private int _consoleWidth = -1;
        public int ConsoleWidth {
            get {
                if (_consoleWidth < 0) {
                    ITextViewMargin leftMargin = WpfTextViewHost.GetTextViewMargin(PredefinedMarginNames.Left);
                    ITextViewMargin rightMargin = WpfTextViewHost.GetTextViewMargin(PredefinedMarginNames.Right);

                    double marginSize = 0.0;
                    if (leftMargin != null && leftMargin.Enabled) {
                        marginSize += leftMargin.MarginSize;
                    }
                    if (rightMargin != null && rightMargin.Enabled) {
                        marginSize += rightMargin.MarginSize;
                    }

                    int n = (int)((WpfTextView.ViewportWidth - marginSize) / WpfTextView.FormattedLineSource.ColumnWidth);
                    _consoleWidth = Math.Max(80, n); // Larger of 80 or n
                }
                return _consoleWidth;
            }
        }

        private void ResetConsoleWidth() {
            _consoleWidth = -1;
        }

        public void Write(string text) {
            if (_inputLineStart == null) // If not in input mode, need unlock to enable output
            {
                SetReadOnlyRegionType(ReadOnlyRegionType.None);
            }

            // Append text to editor buffer
            ITextBuffer textBuffer = WpfTextView.TextBuffer;
            textBuffer.Insert(textBuffer.CurrentSnapshot.Length, text);
            
            // Ensure caret visible (scroll)
            WpfTextView.Caret.EnsureVisible();

            if (_inputLineStart == null) // If not in input mode, need lock again
            {
                SetReadOnlyRegionType(ReadOnlyRegionType.All);
            }
        }

        public void WriteLine(string text) {
            // If append \n only, text becomes 1 line when copied to notepad.
            Write(text + Environment.NewLine);
        }

        public void WriteBackspace() {
            if (_inputLineStart == null) // If not in input mode, need unlock to enable output
            {
                SetReadOnlyRegionType(ReadOnlyRegionType.None);
            }

            // Delete last character from input buffer.
            ITextBuffer textBuffer = WpfTextView.TextBuffer;           
            if (textBuffer.CurrentSnapshot.Length > 0) {
                textBuffer.Delete(new Span(textBuffer.CurrentSnapshot.Length - 1, 1));
            }

            // Ensure caret visible (scroll)
            WpfTextView.Caret.EnsureVisible();

            if (_inputLineStart == null) // If not in input mode, need lock again
            {
                SetReadOnlyRegionType(ReadOnlyRegionType.All);
            }
        }

        public void Write(string text, Color? foreground, Color? background) {
            int begin = WpfTextView.TextSnapshot.Length;
            Write(text);
            int end = WpfTextView.TextSnapshot.Length;

            if (foreground != null || background != null) {
                SnapshotSpan span = new SnapshotSpan(WpfTextView.TextSnapshot, begin, end - begin);
                NewColorSpan.Raise(this, Tuple.Create(span, foreground, background));
            }
        }

        private InputHistory _inputHistory;
        private InputHistory InputHistory {
            get {
                if (_inputHistory == null) {
                    _inputHistory = new InputHistory();
                }
                return _inputHistory;
            }
        }

        private IList<string> _historyInputs;
        private int _currentHistoryInputIndex;

        private void ResetNavigateHistory() {
            _historyInputs = null;
            _currentHistoryInputIndex = -1;
        }

        public void NavigateHistory(int offset) {
            if (_historyInputs == null) {
                _historyInputs = InputHistory.History;
                if (_historyInputs == null) {
                    _historyInputs = new string[] { };
                }

                _currentHistoryInputIndex = _historyInputs.Count;
            }

            int index = _currentHistoryInputIndex + offset;
            if (index >= -1 && index <= _historyInputs.Count) {
                _currentHistoryInputIndex = index;
                string input = (index >= 0 && index < _historyInputs.Count) ? _historyInputs[_currentHistoryInputIndex] : string.Empty;

                // Replace all text after InputLineStart with new text
                WpfTextView.TextBuffer.Replace(AllInputExtent, input);
                WpfTextView.Caret.EnsureVisible();
            }
        }

        private void WriteProgress(string operation, int percentComplete) {
            if (operation == null) {
                throw new ArgumentNullException("operation");
            }

            if (percentComplete < 0) {
                percentComplete = 0;
            }

            if (percentComplete > 100) {
                percentComplete = 100;
            }

            if (percentComplete == 100) {
                HideProgress();
            }
            else {
                VsStatusBar.Progress(
                    ref _pdwCookieForStatusBar,
                    1 /* in progress */,
                    operation,
                    (uint)percentComplete,
                    (uint)100);
            }
        }

        private void HideProgress() {
            VsStatusBar.Progress(
                ref _pdwCookieForStatusBar,
                0 /* completed */,
                String.Empty,
                (uint)100,
                (uint)100);
        }

        public void SetExecutionMode(bool isExecuting) {
            _consoleStatus.SetBusyState(isExecuting);

            if (!isExecuting) {
                HideProgress();

                VsUIShell.UpdateCommandUI(0 /* false = update UI asynchronously */);
            }
        }

        public void Clear() {
            SetReadOnlyRegionType(ReadOnlyRegionType.None);

            ITextBuffer textBuffer = WpfTextView.TextBuffer;
            textBuffer.Delete(new Span(0, textBuffer.CurrentSnapshot.Length));

            // Dispose existing incompleted input line
            _inputLineStart = null;

            // Raise event
            ConsoleCleared.Raise(this);
        }

        public void ClearConsole() {
            if (_inputLineStart != null) {
                Dispatcher.ClearConsole();
            }
        }

        private IVsTextView _view;
        public IVsTextView VsTextView {
            get {
                if (_view == null) {
                    _view = Factory.VsEditorAdaptersFactoryService.CreateVsTextViewAdapter(OleServiceProvider);
                    _view.Initialize(
                        VsTextBuffer as IVsTextLines,
                        IntPtr.Zero,
                        (uint)(TextViewInitFlags.VIF_HSCROLL | TextViewInitFlags.VIF_VSCROLL) | (uint)TextViewInitFlags3.VIF_NO_HWND_SUPPORT,
                        null);

                    // Set font and color
                    IVsTextEditorPropertyCategoryContainer propCategoryContainer = _view as IVsTextEditorPropertyCategoryContainer;
                    if (propCategoryContainer != null) {
                        IVsTextEditorPropertyContainer propContainer;
                        Guid guidPropCategory = EditorDefGuidList.guidEditPropCategoryViewMasterSettings;
                        int hr = propCategoryContainer.GetPropertyCategory(ref guidPropCategory, out propContainer);
                        if (hr == 0) {
                            propContainer.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewGeneral_FontCategory, GuidList.guidPackageManagerConsoleFontAndColorCategory);
                            propContainer.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewGeneral_ColorCategory, GuidList.guidPackageManagerConsoleFontAndColorCategory);
                        }
                    }

                    // add myself as IConsole
                    WpfTextView.TextBuffer.Properties.AddProperty(typeof(IConsole), this);

                    // Initial mark readonly region. Must call Start() to start accepting inputs.
                    SetReadOnlyRegionType(ReadOnlyRegionType.All);

                    // Set some EditorOptions: -DragDropEditing, +WordWrap
                    IEditorOptions editorOptions = Factory.EditorOptionsFactoryService.GetOptions(WpfTextView);
                    editorOptions.SetOptionValue(DefaultTextViewOptions.DragDropEditingId, false);
                    editorOptions.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.WordWrap);

                    // Reset console width when needed
                    WpfTextView.ViewportWidthChanged += (sender, e) => ResetConsoleWidth();
                    WpfTextView.ZoomLevelChanged += (sender, e) => ResetConsoleWidth();

                    // Create my Command Filter
                    new WpfConsoleKeyProcessor(this);
                }

                return _view;
            }
        }

        public object Content {
            get {
                return WpfTextViewHost.HostControl;
            }
        }

        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                var disposable = _dispatcher as IDisposable;
                if (disposable != null) {
                    disposable.Dispose();
                }
            }
        }

        void IDisposable.Dispose() {
            try {
                Dispose(true);
            }
            finally {
                GC.SuppressFinalize(this);
            }
        }

        ~WpfConsole() {
            Dispose(false);
        }

    }
}