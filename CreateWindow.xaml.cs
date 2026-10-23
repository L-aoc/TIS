using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TisViewer.Helpers;
using TisViewer.Models;
using TisViewer.Services;

namespace TisViewer;

public partial class CreateWindow : Window
{
    private const string ItemDragFormat = "TisViewer.CreateItem";
    private static readonly SemaphoreSlim ThumbnailGate = new(4, 4);

    private readonly ObservableCollection<CreateItem> _items = [];
    private readonly Dictionary<string, BitmapSource?> _previewCache = new(StringComparer.OrdinalIgnoreCase);

    private CreateItem? _coverItem;
    private CreateItem? _dragItem;
    private Point _dragStart;
    private bool _outputEdited;
    private bool _settingOutput;
    private bool _isWriting;
    private bool _closeConfirmed;
    private bool _isClosing;
    private CancellationTokenSource? _writeCts;
    private string? _generatedPath;
    private int _previewVersion;

    public CreateWindow(IEnumerable<string>? initialFiles = null)
    {
        InitializeComponent();

        ItemList.ItemsSource = _items;
        _outputEdited = false;
        _settingOutput = false;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _settingOutput = true;
        FolderBox.Text = desktop;
        FileNameBox.Text = "我的相册.tis";
        _settingOutput = false;

        UpdateEmptyState();
        UpdateSummary();
        UpdateSelectionButtons();
        UpdateCoverPreview();

        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_items.Count > 0)
            {
                ItemList.ScrollIntoView(_items[0]);
            }
        });

        if (initialFiles is not null)
        {
            AddPaths(initialFiles, expandFolders: true);
        }
    }

    public string? CreatedArchivePath { get; private set; }

    // ------------------------------------------------------------------
    //  Adding files
    // ------------------------------------------------------------------

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加照片或视频",
            Filter = "媒体文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg;*.3gp;*.ts;*.m2ts|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FileNames, expandFolders: false);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含照片或视频的文件夹",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddPaths([dialog.FolderName], expandFolders: true);
        }
    }

    private void AddPaths(IEnumerable<string> paths, bool expandFolders)
    {
        var before = _items.Count;
        var addedItems = new List<CreateItem>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                if (!expandFolders)
                {
                    continue;
                }

                try
                {
                    var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Where(TisMediaTypes.IsSupportedMedia)
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var file in files)
                    {
                        var item = TryAddFile(file);
                        if (item is not null)
                        {
                            addedItems.Add(item);
                        }
                    }
                }
                catch
                {
                    // ignored unreadable folders
                }
            }
            else if (File.Exists(path))
            {
                var item = TryAddFile(path);
                if (item is not null)
                {
                    addedItems.Add(item);
                }
            }
        }

        if (_items.Count == before)
        {
            return;
        }

        if (_coverItem is null)
        {
            _coverItem = _items[0];
        }

        Reindex();
        UpdateEmptyState();
        UpdateSummary();
        UpdateCoverState();
        UpdateCoverPreview();
        UpdateSelectionButtons();

        if (!_outputEdited)
        {
            SetDefaultOutput();
        }

        foreach (var item in addedItems)
        {
            _ = LoadThumbnailAsync(item);
        }
    }

    private CreateItem? TryAddFile(string path)
    {
        if (!File.Exists(path) || !TisMediaTypes.IsSupportedMedia(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (_items.Any(i => string.Equals(i.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var info = new FileInfo(fullPath);
        var item = new CreateItem
        {
            SourcePath = fullPath,
            Name = Path.GetFileName(fullPath),
            Extension = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(),
            Kind = TisMediaTypes.Detect(fullPath),
            SizeBytes = info.Length,
            Index = _items.Count,
        };

        _items.Add(item);
        return item;
    }

    private async Task LoadThumbnailAsync(CreateItem item)
    {
        await ThumbnailGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var thumbnail = await Task.Run(() => item.Kind == TisMediaKind.Video
                ? ShellIntegration.GetThumbnail(item.SourcePath, 160)
                : ImageLoader.TryDecodeFile(item.SourcePath, 160)).ConfigureAwait(false);

            if (_items.Contains(item))
            {
                await Dispatcher.InvokeAsync(() => item.Thumbnail = thumbnail);
            }
        }
        catch
        {
            // thumbnail is optional
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    private void SetDefaultOutput()
    {
        if (_items.Count == 0)
        {
            return;
        }

        var folder = Path.GetDirectoryName(_items[0].SourcePath) ?? string.Empty;
        var name = "我的相册";
        try
        {
            var folderName = new DirectoryInfo(folder).Name;
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                name = folderName;
            }
        }
        catch
        {
            // keep default
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (name.Length > 60)
        {
            name = name[..60];
        }

        _settingOutput = true;
        FolderBox.Text = folder;
        FileNameBox.Text = name + ".tis";
        _settingOutput = false;
        _outputEdited = false;
    }

    // ------------------------------------------------------------------
    //  List bookkeeping
    // ------------------------------------------------------------------

    private void Reindex()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Index = i;
        }
    }

    private void UpdateEmptyState()
    {
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = _items.Count > 0 && !_isWriting;
    }

    private void UpdateSummary()
    {
        var total = _items.Sum(i => i.SizeBytes);
        SummaryText.Text = _items.Count == 0
            ? "共 0 项"
            : $"共 {_items.Count} 项 · {TisMediaItem.FormatSize(total)}";
    }

    private void UpdateSelectionButtons()
    {
        var hasSelection = ItemList.SelectedItem is CreateItem;
        MoveUpButton.IsEnabled = hasSelection;
        MoveDownButton.IsEnabled = hasSelection;
        RemoveButton.IsEnabled = hasSelection;
        SetCoverButton.IsEnabled = hasSelection;
        ClearCoverButton.IsEnabled = _coverItem is not null;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionButtons();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (ItemList.SelectedItem is not CreateItem item)
        {
            return;
        }

        var index = _items.IndexOf(item);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= _items.Count)
        {
            return;
        }

        _items.Move(index, next);
        Reindex();
        ItemList.SelectedItem = item;
        ItemList.ScrollIntoView(item);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemList.SelectedItems.Cast<CreateItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var item in selected)
        {
            _items.Remove(item);
            if (ReferenceEquals(_coverItem, item))
            {
                _coverItem = null;
            }
        }

        if (_coverItem is null)
        {
            _coverItem = _items.FirstOrDefault();
        }

        Reindex();
        UpdateEmptyState();
        UpdateSummary();
        UpdateCoverState();
        UpdateCoverPreview();
        UpdateSelectionButtons();
    }

    // ------------------------------------------------------------------
    //  Cover
    // ------------------------------------------------------------------

    private void SetCover_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not CreateItem item)
        {
            return;
        }

        _coverItem = item;
        UpdateCoverState();
        UpdateCoverPreview();
    }

    private void ClearCover_Click(object sender, RoutedEventArgs e)
    {
        _coverItem = null;
        UpdateCoverState();
        UpdateCoverPreview();
    }

    private void UpdateCoverState()
    {
        foreach (var item in _items)
        {
            item.IsCover = ReferenceEquals(item, _coverItem);
        }

        ClearCoverButton.IsEnabled = _coverItem is not null;
    }

    private async void UpdateCoverPreview()
    {
        var version = ++_previewVersion;
        var item = _coverItem ?? _items.FirstOrDefault();

        if (item is null)
        {
            CoverPreview.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            CoverPlaceholder.Text = "\uE91B";
            CoverCaption.Text = "未选择封面";
            return;
        }

        CoverPlaceholder.Text = item.Kind == TisMediaKind.Video ? "\uE714" : "\uE91B";
        CoverCaption.Text = _coverItem is null ? $"默认封面 · {item.Name}" : item.Name;

        if (_previewCache.TryGetValue(item.SourcePath, out var cached))
        {
            ApplyCoverPreview(cached, version);
            return;
        }

        try
        {
            var thumbnail = await Task.Run(() => item.Kind == TisMediaKind.Video
                ? ShellIntegration.GetThumbnail(item.SourcePath, 640)
                : ImageLoader.TryDecodeFile(item.SourcePath, 640));

            _previewCache[item.SourcePath] = thumbnail;
            ApplyCoverPreview(thumbnail, version);
        }
        catch
        {
            ApplyCoverPreview(null, version);
        }
    }

    private void ApplyCoverPreview(BitmapSource? thumbnail, int version)
    {
        if (version != _previewVersion)
        {
            return;
        }

        CoverPreview.Source = thumbnail;
        CoverPlaceholder.Visibility = thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    //  Output selection
    // ------------------------------------------------------------------

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingOutput)
        {
            _outputEdited = true;
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择保存位置",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            _outputEdited = true;
        }
    }

    private bool TryBuildOutputPath(out string outputPath, out string error)
    {
        outputPath = string.Empty;
        error = string.Empty;

        var name = FileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "请填写文件名。";
            return false;
        }

        try
        {
            name = Path.GetFileName(name);
        }
        catch
        {
            error = "文件名包含无效字符。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "文件名无效。";
            return false;
        }

        if (!name.EndsWith(".tis", StringComparison.OrdinalIgnoreCase))
        {
            name += ".tis";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "文件名包含无效字符。";
            return false;
        }

        var folder = FolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            error = "保存位置不存在，请重新选择文件夹。";
            return false;
        }

        outputPath = Path.Combine(folder, name);
        return true;
    }

    // ------------------------------------------------------------------
    //  Generation
    // ------------------------------------------------------------------

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_isWriting || _items.Count == 0)
        {
            return;
        }

        if (!TryBuildOutputPath(out var outputPath, out var error))
        {
            MessageBox.Show(this, error, "TIS 相册", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (File.Exists(outputPath))
        {
            var result = MessageBox.Show(this,
                $"文件已存在：\n{outputPath}\n\n要覆盖它吗？",
                "TIS 相册", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var request = new TisCreateRequest
        {
            OutputPath = outputPath,
            Files = _items.Select(i => i.SourcePath).ToList(),
            CoverSourcePath = (_coverItem ?? _items[0]).SourcePath,
            Compression = GetCompressionPreset(),
        };

        _isWriting = true;
        GenerateButton.IsEnabled = false;
        _writeCts = new CancellationTokenSource();
        SuccessOverlay.Visibility = Visibility.Collapsed;
        ProgressOverlay.Visibility = Visibility.Visible;
        ProgressBarControl.Value = 0;
        ProgressText.Text = "准备中…";
        ProgressDetail.Text = string.Empty;

        var progress = new Progress<TisWriteProgress>(p =>
        {
            ProgressBarControl.Value = Math.Clamp(p.Fraction * 100, 0, 100);
            ProgressText.Text = p.Completed >= p.Total ? "正在完成…" : $"正在处理 {Math.Min(p.Completed + 1, p.Total)} / {p.Total}";
            ProgressDetail.Text = p.CurrentFile;
        });

        try
        {
            await TisWriter.WriteAsync(request, progress, _writeCts.Token);
            _generatedPath = outputPath;
            _closeConfirmed = true;
            ProgressOverlay.Visibility = Visibility.Collapsed;
            SuccessPath.Text = outputPath;
            SuccessOverlay.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            ProgressOverlay.Visibility = Visibility.Collapsed;
            if (!_isClosing)
            {
                MessageBox.Show(this, "已取消生成，未完成的文件已清理。", "TIS 相册", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ProgressOverlay.Visibility = Visibility.Collapsed;
            if (!_isClosing)
            {
                MessageBox.Show(this, "生成失败：\n\n" + ex.Message, "TIS 相册", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isWriting = false;
            _writeCts?.Dispose();
            _writeCts = null;
            GenerateButton.IsEnabled = _items.Count > 0;
        }
    }

    private TisCompressionPreset GetCompressionPreset()
    {
        foreach (var radio in FindVisualChildren<RadioButton>(this))
        {
            if (radio.IsChecked == true && radio.Tag is string tag &&
                Enum.TryParse<TisCompressionPreset>(tag, out var preset))
            {
                return preset;
            }
        }

        return TisCompressionPreset.Balanced;
    }

    private void CancelGeneration_Click(object sender, RoutedEventArgs e)
    {
        _writeCts?.Cancel();
        ProgressText.Text = "正在取消…";
    }

    private void OpenGenerated_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_generatedPath))
        {
            return;
        }

        CreatedArchivePath = _generatedPath;
        _closeConfirmed = true;
        try
        {
            DialogResult = true;
        }
        catch
        {
            Close();
        }
    }

    private void ShowGenerated_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_generatedPath))
        {
            ShellIntegration.ShowInExplorer(_generatedPath);
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _closeConfirmed = true;
        Close();
    }

    // ------------------------------------------------------------------
    //  Drag and drop / reordering
    // ------------------------------------------------------------------

    private void ItemList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is CreateItem item)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(ItemList);
        }
    }

    private void ItemList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
        {
            return;
        }

        var position = e.GetPosition(ItemList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(ItemList, new DataObject(ItemDragFormat, dragged), DragDropEffects.Move);
    }

    private void ItemList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ItemDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ItemList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ItemDragFormat) is CreateItem dragged)
        {
            var oldIndex = _items.IndexOf(dragged);
            if (oldIndex < 0)
            {
                return;
            }

            var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            var insertIndex = _items.Count;

            if (target?.DataContext is CreateItem targetItem)
            {
                insertIndex = _items.IndexOf(targetItem);
                var point = e.GetPosition(target);
                if (point.Y > target.ActualHeight / 2)
                {
                    insertIndex++;
                }
            }

            if (insertIndex > oldIndex)
            {
                insertIndex--;
            }

            insertIndex = Math.Clamp(insertIndex, 0, _items.Count - 1);
            if (insertIndex != oldIndex)
            {
                _items.Move(oldIndex, insertIndex);
                Reindex();
                ItemList.SelectedItem = dragged;
                ItemList.ScrollIntoView(dragged);
            }

            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddPaths(files, expandFolders: true);
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(f => Directory.Exists(f) || (File.Exists(f) && TisMediaTypes.IsSupportedMedia(f))))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddPaths(files, expandFolders: true);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    // ------------------------------------------------------------------
    //  Window chrome / closing
    // ------------------------------------------------------------------

    private void Window_Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Window_Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_isWriting)
        {
            var result = MessageBox.Show(this,
                "TIS 文件正在生成。要取消生成并关闭窗口吗？",
                "TIS 相册", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _isClosing = true;
            _writeCts?.Cancel();
            _closeConfirmed = true;
            return;
        }

        if (CreatedArchivePath is not null || _closeConfirmed)
        {
            return;
        }

        if (_items.Count == 0)
        {
            return;
        }

        var discard = MessageBox.Show(this,
            "当前列表还没有生成 TIS 文件，确定要关闭吗？",
            "TIS 相册", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (discard != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _closeConfirmed = true;
    }

    public sealed class CreateItem : INotifyPropertyChanged
    {
        private int _index;
        private bool _isCover;
        private BitmapSource? _thumbnail;

        public string SourcePath { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Extension { get; init; } = string.Empty;

        public TisMediaKind Kind { get; init; }

        public long SizeBytes { get; init; }

        public string Glyph => Kind == TisMediaKind.Video ? "\uE714" : "\uE91B";

        public bool IsVideo => Kind == TisMediaKind.Video;

        public string Meta => $"{KindLabel} · {TisMediaItem.FormatSize(SizeBytes)} · {Extension}";

        public string KindLabel => Kind == TisMediaKind.Video ? "视频" : "图片";

        public int Index
        {
            get => _index;
            set
            {
                if (_index == value)
                {
                    return;
                }

                _index = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Index)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexText)));
            }
        }

        public string IndexText => (Index + 1).ToString("00");

        public bool IsCover
        {
            get => _isCover;
            set
            {
                if (_isCover == value)
                {
                    return;
                }

                _isCover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCover)));
            }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value))
                {
                    return;
                }

                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
