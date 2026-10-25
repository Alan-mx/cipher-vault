using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Host.Client;
using Microsoft.Msagl.Drawing;
using Microsoft.Msagl.WpfGraphControl;
using MsaglColor = Microsoft.Msagl.Drawing.Color;
using MsaglNode = Microsoft.Msagl.Drawing.Node;

namespace CipherVault.Ui.Wpf;

public partial class DocumentWorkspaceView : UserControl
{
    private IHostVaultClient? _service;
    private Guid? _documentId;
    private VaultDocumentMap? _map;
    private string _originalContent = string.Empty;
    private string? _backlinkCursor;
    private bool _suppressDirty;
    private bool _suppressTitleDirty;
    private bool _busy;
    private bool _linkTargetsLoading;
    private List<VaultCatalogItem>? _linkTargets;
    private WikiLinkCompletion? _linkCompletion;
    private GraphViewer? _graphViewer;

    public DocumentWorkspaceView()
    {
        InitializeComponent();
    }

    public event EventHandler? DocumentSaved;

    public bool HasUnsavedChanges =>
        _documentId is not null &&
        _map is not null &&
        (!string.Equals(
            EditorBox.Text,
            _originalContent,
            StringComparison.Ordinal) ||
         !string.Equals(
             TitleEditorBox.Text.Trim(),
             _map.Title,
             StringComparison.Ordinal));

    public void Initialize(IHostVaultClient service)
    {
        _service = service;
    }

    public void Clear()
    {
        _documentId = null;
        _map = null;
        _originalContent = string.Empty;
        _backlinkCursor = null;
        _linkTargets = null;
        _suppressDirty = true;
        _suppressTitleDirty = true;
        EditorBox.Clear();
        TitleEditorBox.Clear();
        _suppressDirty = false;
        _suppressTitleDirty = false;
        SyncBacklinksCheckBox.IsChecked = true;
        PreviewBox.Document = new FlowDocument();
        DocumentTitle.Text = "选择一篇文档";
        DocumentMeta.Text = string.Empty;
        BacklinkList.ItemsSource = null;
        UnresolvedList.ItemsSource = null;
        BacklinkSummary.Text = "尚无反向链接";
        UnresolvedSummary.Text = "没有未解析链接";
        LoadMoreBacklinksButton.IsEnabled = false;
        CloseLinkSuggestions();
        ResetGraph();
        SetEditMode(false);
        UpdateButtons();
    }

    public async Task<bool> LoadDocumentAsync(Guid documentId)
    {
        if (_service is null || _busy)
        {
            return false;
        }
        if (_documentId == documentId && _map is not null)
        {
            return true;
        }
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return false;
        }

        _busy = true;
        try
        {
            var content = await ReadFullDocumentAsync(documentId).ConfigureAwait(true);
            var map = await _service.GetDocumentMapAsync(documentId).ConfigureAwait(true);
            _documentId = documentId;
            _map = map;
            _originalContent = content;
            _suppressDirty = true;
            _suppressTitleDirty = true;
            EditorBox.Text = content;
            TitleEditorBox.Text = map.Title;
            _suppressDirty = false;
            _suppressTitleDirty = false;
            _linkTargets = null;
            DocumentTitle.Text = map.Title;
            DocumentMeta.Text =
                $"{map.VirtualPath}  |  版本 {map.Revision}  |  " +
                $"{map.OutgoingLinks.Count} 个出链  |  {map.BacklinkCount} 条反向链接";
            RenderPreview();
            PopulateUnresolvedLinks();
            _backlinkCursor = null;
            await LoadBacklinksAsync(reset: true).ConfigureAwait(true);
            SetEditMode(false);
            UpdateButtons();
            ResetGraph();
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }
        var decision = MessageBox.Show(
            Window.GetWindow(this),
            "当前文档有未保存修改。是否先保存？",
            "CipherVault",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return decision switch
        {
            MessageBoxResult.Yes => await SaveAsync().ConfigureAwait(true),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private async Task<string> ReadFullDocumentAsync(Guid documentId)
    {
        if (_service is null)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var page = await _service.ReadNoteAsync(
                documentId,
                offset,
                16_000).ConfigureAwait(true);
            builder.Append(page.Content);
            if (page.Done)
            {
                return builder.ToString();
            }
            if (page.NextOffset <= offset)
            {
                throw new InvalidOperationException(
                    "Document pagination did not advance.");
            }
            offset = page.NextOffset;
        }
    }

    private void RenderPreview()
    {
        if (_map is null)
        {
            PreviewBox.Document = new FlowDocument();
            return;
        }
        PreviewBox.Document = MarkdownWpfRenderer.Render(
            _originalContent,
            _map.OutgoingLinks,
            OnHyperlinkClicked);
    }

    private void PopulateUnresolvedLinks()
    {
        var items = _map?.OutgoingLinks
            .Where(link => !string.Equals(
                link.ResolutionStatus,
                "resolved",
                StringComparison.Ordinal))
            .Select(link => new UnresolvedLinkItem(
                link,
                link.DisplayText,
                string.Equals(
                    link.ResolutionStatus,
                    "ambiguous",
                    StringComparison.Ordinal)
                    ? $"存在 {link.Candidates.Count} 个候选文档"
                    : "目标文档不存在"))
            .ToArray() ?? [];
        UnresolvedList.ItemsSource = items;
        UnresolvedSummary.Text = items.Length == 0
            ? "没有未解析链接"
            : $"{items.Length} 个未解析或歧义链接";
    }

    private async Task LoadBacklinksAsync(bool reset)
    {
        if (_service is null || _documentId is null)
        {
            return;
        }
        var page = await _service.ListBacklinksAsync(
            _documentId.Value,
            reset ? null : _backlinkCursor,
            50).ConfigureAwait(true);
        _backlinkCursor = page.NextCursor;
        var items = page.Items.Select(item => new BacklinkListItem(
            item.SourceDocumentId,
            item.SourceTitle,
            $"{item.SourceVirtualPath}  |  引用 {item.OccurrenceCount} 次"))
            .ToList();
        if (reset)
        {
            BacklinkList.ItemsSource = items;
        }
        else if (BacklinkList.ItemsSource is IEnumerable<BacklinkListItem> existing)
        {
            var combined = existing.Concat(items).ToList();
            BacklinkList.ItemsSource = combined;
        }
        var count = (BacklinkList.ItemsSource as IEnumerable<BacklinkListItem>)?
            .Count() ?? 0;
        BacklinkSummary.Text = count == 0
            ? "尚无反向链接"
            : $"显示 {count} 个来源文档";
        LoadMoreBacklinksButton.IsEnabled = page.NextCursor is not null;
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        _linkTargets = null;
        SyncBacklinksCheckBox.IsChecked = true;
        SetEditMode(true);
        TitleEditorBox.Focus();
        TitleEditorBox.SelectAll();
        await Task.CompletedTask;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync();
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }
        _suppressDirty = true;
        _suppressTitleDirty = true;
        EditorBox.Text = _originalContent;
        TitleEditorBox.Text = _map?.Title ?? string.Empty;
        _suppressDirty = false;
        _suppressTitleDirty = false;
        SetEditMode(false);
        UpdateButtons();
    }

    private async Task<bool> SaveAsync()
    {
        if (_service is null || _map is null || _documentId is null ||
            !HasUnsavedChanges)
        {
            SetEditMode(false);
            return true;
        }
        try
        {
            var title = TitleEditorBox.Text.Trim();
            var titlePatch = string.Equals(
                title,
                _map.Title,
                StringComparison.Ordinal)
                ? null
                : title;
            await _service.UpdateNoteAsync(
                _documentId.Value,
                _map.Revision,
                new VaultNotePatch(
                    Title: titlePatch,
                    Folder: null,
                    Content: EditorBox.Text,
                    Tags: null,
                    UpdateBacklinks: SyncBacklinksCheckBox.IsChecked == true))
                .ConfigureAwait(true);
            _linkTargets = null;
            _documentId = null;
            var loaded = await LoadDocumentAsync(_map.DocumentId)
                .ConfigureAwait(true);
            if (loaded)
            {
                DocumentSaved?.Invoke(this, EventArgs.Empty);
            }
            return loaded;
        }
        catch (Exception exception) when (IsRevisionConflict(exception))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "文档已被其他操作修改。当前编辑内容已保留，请取消后重新打开文档。",
                "版本冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirty)
        {
            return;
        }
        UpdateButtons();
        UpdateLinkSuggestions();
    }

    private void EditorBox_SelectionChanged(
        object sender,
        RoutedEventArgs e)
    {
        UpdateLinkSuggestions();
    }

    private void TitleEditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTitleDirty)
        {
            return;
        }
        UpdateButtons();
    }

    private async Task EnsureLinkTargetsAsync()
    {
        if (_service is null || _linkTargets is not null || _linkTargetsLoading)
        {
            return;
        }
        _linkTargetsLoading = true;
        try
        {
            var items = new List<VaultCatalogItem>();
            string? cursor = null;
            do
            {
                var page = await _service.ListCatalogAsync(
                    null,
                    cursor,
                    100).ConfigureAwait(true);
                items.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
            _linkTargets = items;
            if (EditorBox.Visibility == Visibility.Visible)
            {
                UpdateLinkSuggestions();
            }
        }
        catch
        {
            _linkTargets = [];
            CloseLinkSuggestions();
        }
        finally
        {
            _linkTargetsLoading = false;
        }
    }

    private void UpdateLinkSuggestions()
    {
        if (_service is null ||
            EditorBox.Visibility != Visibility.Visible ||
            _suppressDirty)
        {
            CloseLinkSuggestions();
            return;
        }
        var completion = LinkSuggestionEngine.FindCompletion(
            EditorBox.Text,
            EditorBox.CaretIndex);
        if (completion is null)
        {
            CloseLinkSuggestions();
            return;
        }
        _linkCompletion = completion;
        if (_linkTargets is null)
        {
            CloseLinkSuggestions(preserveCompletion: true);
            _ = EnsureLinkTargetsAsync();
            return;
        }
        var suggestions = LinkSuggestionEngine.Rank(
            _linkTargets,
            _documentId,
            completion.Query);
        if (suggestions.Count == 0)
        {
            CloseLinkSuggestions();
            return;
        }
        LinkSuggestionList.ItemsSource = suggestions;
        LinkSuggestionList.SelectedIndex = 0;
        PositionLinkSuggestions();
    }

    private void PositionLinkSuggestions()
    {
        var rect = EditorBox.GetRectFromCharacterIndex(EditorBox.CaretIndex);
        if (rect.IsEmpty)
        {
            LinkSuggestionPopup.HorizontalOffset = 0;
            LinkSuggestionPopup.VerticalOffset = Math.Max(0, EditorBox.ActualHeight);
        }
        else
        {
            LinkSuggestionPopup.HorizontalOffset = Math.Max(0, rect.Left);
            LinkSuggestionPopup.VerticalOffset = Math.Max(0, rect.Bottom + 2);
        }
        LinkSuggestionPopup.IsOpen = true;
    }

    private void CloseLinkSuggestions(bool preserveCompletion = false)
    {
        LinkSuggestionPopup.IsOpen = false;
        LinkSuggestionList.ItemsSource = null;
        if (!preserveCompletion)
        {
            _linkCompletion = null;
        }
    }

    private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!LinkSuggestionPopup.IsOpen ||
            LinkSuggestionList.Items.Count == 0)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                LinkSuggestionList.SelectedIndex = Math.Min(
                    LinkSuggestionList.SelectedIndex + 1,
                    LinkSuggestionList.Items.Count - 1);
                LinkSuggestionList.ScrollIntoView(
                    LinkSuggestionList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                LinkSuggestionList.SelectedIndex = Math.Max(
                    LinkSuggestionList.SelectedIndex - 1,
                    0);
                LinkSuggestionList.ScrollIntoView(
                    LinkSuggestionList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (LinkSuggestionList.SelectedItem is LinkSuggestion suggestion)
                {
                    AcceptLinkSuggestion(suggestion);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CloseLinkSuggestions();
                e.Handled = true;
                break;
        }
    }

    private void LinkSuggestionList_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(
            LinkSuggestionList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not LinkSuggestion suggestion)
        {
            return;
        }
        AcceptLinkSuggestion(suggestion);
        e.Handled = true;
    }

    private void AcceptLinkSuggestion(LinkSuggestion suggestion)
    {
        if (_linkCompletion is not { } completion)
        {
            return;
        }
        var text = EditorBox.Text;
        if (completion.StartOffset < 0 ||
            completion.CaretOffset > text.Length ||
            completion.StartOffset >= completion.CaretOffset)
        {
            CloseLinkSuggestions();
            return;
        }
        var hasClosing = completion.CaretOffset + 1 < text.Length &&
            text[completion.CaretOffset] == ']' &&
            text[completion.CaretOffset + 1] == ']';
        var replacement = $"[[{suggestion.InsertTarget}" +
            (hasClosing ? string.Empty : "]]");
        var newCaret = completion.StartOffset +
            replacement.Length +
            (hasClosing ? 2 : 0);
        _suppressDirty = true;
        EditorBox.Text = text
            .Remove(
                completion.StartOffset,
                completion.CaretOffset - completion.StartOffset)
            .Insert(completion.StartOffset, replacement);
        EditorBox.CaretIndex = newCaret;
        _suppressDirty = false;
        CloseLinkSuggestions();
        UpdateButtons();
        EditorBox.Focus();
    }

    private void SetEditMode(bool editing)
    {
        EditorBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        PreviewBox.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        DocumentTitle.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        TitleEditorBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SyncBacklinksCheckBox.Visibility = editing
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!editing)
        {
            CloseLinkSuggestions();
        }
        EditButton.IsEnabled = !editing && HasDocument();
        SaveButton.IsEnabled = editing && HasUnsavedChanges;
        CancelButton.IsEnabled = editing;
    }

    private void UpdateButtons()
    {
        var editing = EditorBox.Visibility == Visibility.Visible;
        EditButton.IsEnabled = !editing && HasDocument();
        SaveButton.IsEnabled = editing && HasUnsavedChanges;
        CancelButton.IsEnabled = editing;
    }

    private bool HasDocument()
    {
        return _documentId is not null && _map is not null;
    }

    private async void LoadMoreBacklinksButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadBacklinksAsync(reset: false);
    }

    private async void BacklinkList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (BacklinkList.SelectedItem is BacklinkListItem item)
        {
            await LoadDocumentAsync(item.DocumentId);
        }
    }

    private async void UnresolvedList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        await CreateFromUnresolvedAsync();
    }

    private async Task CreateFromUnresolvedAsync()
    {
        if (_service is null ||
            UnresolvedList.SelectedItem is not UnresolvedLinkItem item)
        {
            return;
        }
        if (string.Equals(
                item.Link.ResolutionStatus,
                "ambiguous",
                StringComparison.Ordinal))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "该链接对应多个候选文档，无法自动创建。",
                "链接不唯一",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var normalized = item.Link.RawTarget
            .Replace('\\', '/')
            .Trim('/');
        var separator = normalized.LastIndexOf('/');
        var folder = separator > 0 ? normalized[..separator] : null;
        var title = separator >= 0
            ? normalized[(separator + 1)..]
            : normalized;
        if (title.Length == 0)
        {
            return;
        }
        if (MessageBox.Show(
                Window.GetWindow(this),
                $"创建文档“{title}”？",
                "创建链接目标",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _service.CreateNoteAsync(
                title,
                folder,
                "md",
                $"# {title}\n",
                null).ConfigureAwait(true);
            _linkTargets = null;
            await LoadDocumentAsync(result.DocumentId).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnHyperlinkClicked(string target)
    {
        if (target.StartsWith(
                "ciphervault://document/",
                StringComparison.OrdinalIgnoreCase))
        {
            var idText = target["ciphervault://document/".Length..];
            if (Guid.TryParse(idText, out var documentId))
            {
                await LoadDocumentAsync(documentId);
            }
            return;
        }
        if (target.StartsWith(
                "ciphervault://unresolved/",
                StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                target["ciphervault://unresolved/".Length..],
                out var index) &&
            _map is not null &&
            index >= 0 &&
            index < _map.OutgoingLinks.Count)
        {
            var link = _map.OutgoingLinks[index];
            UnresolvedList.SelectedItem = UnresolvedList.ItemsSource
                is IEnumerable<UnresolvedLinkItem> unresolved
                ? unresolved.FirstOrDefault(item => ReferenceEquals(item.Link, link))
                : null;
            await CreateFromUnresolvedAsync();
            return;
        }
        if ((target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) &&
            MessageBox.Show(
                Window.GetWindow(this),
                "在系统浏览器中打开此外部链接？",
                "外部链接",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true
            });
        }
    }

    private async void RefreshGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _documentId is null)
        {
            return;
        }
        try
        {
            var depth = GraphDepthComboBox.SelectedIndex == 1 ? 2 : 1;
            var tags = GraphTagsTextBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries);
            var query = new VaultLocalGraphQuery(
                _documentId.Value,
                depth,
                tags,
                string.IsNullOrWhiteSpace(GraphFolderTextBox.Text)
                    ? null
                    : GraphFolderTextBox.Text.Trim(),
                200,
                500);
            var graph = await _service.GetLocalGraphAsync(query).ConfigureAwait(true);
            RenderGraph(graph);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RenderGraph(VaultLocalGraphResult result)
    {
        var graph = new Graph("local");
        graph.Attr.LayerDirection = LayerDirection.LR;
        foreach (var node in result.Nodes)
        {
            var drawingNode = graph.AddNode(
                node.DocumentId.ToString("D").ToLowerInvariant());
            drawingNode.LabelText = node.Title;
            drawingNode.Attr.Shape = Shape.Box;
            drawingNode.Attr.FillColor = node.IsCenter
                ? new MsaglColor(208, 225, 255)
                : new MsaglColor(255, 255, 255);
            drawingNode.UserData = node.DocumentId;
        }
        foreach (var edge in result.Edges)
        {
            var drawingEdge = graph.AddEdge(
                edge.SourceDocumentId.ToString("D").ToLowerInvariant(),
                edge.TargetDocumentId.ToString("D").ToLowerInvariant());
            drawingEdge.UserData = new GraphEdgeInfo(
                result.Nodes.FirstOrDefault(node =>
                    node.DocumentId == edge.SourceDocumentId)?.Title
                    ?? edge.SourceDocumentId.ToString("D"),
                result.Nodes.FirstOrDefault(node =>
                    node.DocumentId == edge.TargetDocumentId)?.Title
                    ?? edge.TargetDocumentId.ToString("D"),
                edge.RelationType,
                edge.LinkCount);
            if (edge.LinkCount > 1)
            {
                drawingEdge.LabelText = $"×{edge.LinkCount}";
            }
        }

        var canvas = new Canvas();
        GraphHost.Child = canvas;
        var viewer = new GraphViewer
        {
            RunLayoutAsync = true
        };
        viewer.BindToPanel(canvas);
        viewer.LayoutComplete += (_, _) => viewer.SetInitialTransform();
        canvas.MouseLeftButtonDown += GraphCanvas_MouseLeftButtonDown;
        _graphViewer = viewer;
        viewer.Graph = graph;
        GraphSummary.Text =
            $"{result.Nodes.Count} 个节点，{result.Edges.Count} 条边" +
            (result.Truncated ? "，结果已截断" : string.Empty);
    }

    private void GraphCanvas_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_graphViewer?.ObjectUnderMouseCursor is IViewerNode viewerNode &&
            viewerNode.Node.UserData is Guid documentId)
        {
            _ = LoadDocumentAsync(documentId);
            return;
        }
        if (_graphViewer?.ObjectUnderMouseCursor is IViewerEdge viewerEdge &&
            viewerEdge.Edge.UserData is GraphEdgeInfo edgeInfo)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"{edgeInfo.SourceTitle} → {edgeInfo.TargetTitle}\n" +
                $"{edgeInfo.RelationType}  |  {edgeInfo.LinkCount} 条链接",
                "关系",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void ResetGraph()
    {
        GraphHost.Child = null;
        GraphSummary.Text = "点击“刷新图谱”加载当前文档关系";
        _graphViewer = null;
    }

    private static bool IsRevisionConflict(Exception exception)
    {
        return exception switch
        {
            VaultException { Code: VaultErrorCode.Conflict } => true,
            PipeRpcException { Code: "revision_conflict" } => true,
            _ => false
        };
    }

    private void ShowError(Exception exception)
    {
        MessageBox.Show(
            Window.GetWindow(this),
            exception.Message,
            "CipherVault",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private sealed record BacklinkListItem(
        Guid DocumentId,
        string Summary,
        string Detail);

    private sealed record UnresolvedLinkItem(
        VaultOutgoingLink Link,
        string Summary,
        string Detail);

    private sealed record GraphEdgeInfo(
        string SourceTitle,
        string TargetTitle,
        string RelationType,
        int LinkCount);
}
