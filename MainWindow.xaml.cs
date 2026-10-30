using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using TisViewer.Helpers;
using TisViewer.Models;
using TisViewer.Services;

namespace TisViewer;

public partial class MainWindow : Window
{
    private const double ToolbarHeight = 42;
    private const double HomeWidth = 1000;
    private const double HomeHeight = 700;
    private const double ViewerMinWidth = 560;
    private const double ViewerMinHeight = 360;
    private const double ImageMaxDimension = 4096;
    private const int ImageCacheLimit = 3;

    private readonly MediaCache _cache = new();
    private readonly ObservableCollection<RecentCard> _recentCards = [];
    private readonly Dictionary<string, ArchivePreview> _recentPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _imageCacheOrder = new();
    private readonly DispatcherTimer _videoTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly ScaleTransform _imageScale = new(1, 1);
    private readonly TranslateTransform _imageTranslate = new(0, 0);

    private TisArchive? _archive;
    private int _index = -1;
    private int _loadGeneration;
    private int _openGeneration;
    private int _recentLoadVersion;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _prefetchCts;
    private bool _isClosing;
    private bool _isFullscreen;
    private WindowState _restoreState = WindowState.Normal;
    private GridLength _restoreTitleHeight = new(ToolbarHeight);
    private Rect _homeBounds;
    private bool _hasHomeBounds;
    private Rect _preFullscreenBounds;

    private bool _isSeeking;
    private bool _suppressSlider;
    private bool _videoReady;
    private bool _isPlaying;
    private bool _videoMuted;
    private bool _isPanning;
    private double _zoom = 1;
    private Point _panStart;
    private Point _panOrigin;
    private string? _currentVideoPath;
    private string? _cachedArchivePath;

    public MainWindow()
    {
        InitializeComponent();

        _imageScale.ScaleX = 1;
        _imageScale.ScaleY = 1;
        ImageHost.RenderTransform = new TransformGroup { Children = { _imageScale, _imageTranslate } };

        RecentList.ItemsSource = _recentCards;

        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _videoTimer.Tick += (_, _) => UpdateVideoProgress();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        SpinnerTransform.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });

        Task.Run(() => _cache.CleanupOldCache(TimeSpan.FromDays(14)));
    }

    private bool IsViewerVisible => ViewerView.Visibility == Visibility.Visible;

    private TisMediaItem? CurrentItem =>
        _archive is not null && _index >= 0 && _index < _archive.Items.Count ? _archive.Items[_index] : null;

    // ------------------------------------------------------------------
    //  Opening / closing
    // ------------------------------------------------------------------

    public async void OpenFile(string path)
    {
        if (_isClosing || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            ShowToast("文件不存在，可能已被移动或删除");
            return;
        }

        var wasHome = !IsViewerVisible;

        try
        {
            var openGeneration = ++_openGeneration;

            if (wasHome)
            {
                _homeBounds = WindowState == WindowState.Maximized
                    ? RestoreBounds
                    : new Rect(Left, Top, Width, Height);
                _hasHomeBounds = true;

                EnterViewerChrome();
                SetLoadingWindowSize();
            }

            CloseVideo();
            CancelPrefetch();
            ShowLoadingOverlay("正在打开相册…");

            var archive = await Task.Run(() => TisArchive.Open(path));
            if (_isClosing || openGeneration != _openGeneration)
            {
                archive.Dispose();
                return;
            }

            CancelLoad();
            CancelPrefetch();
            CloseVideo();
            if (!string.Equals(_cachedArchivePath, archive.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                ClearImageCache();
            }

            _cachedArchivePath = archive.FilePath;
            _archive?.Dispose();
            _archive = archive;
            _index = archive.CoverIndex >= 0 ? archive.CoverIndex : 0;

            RecentFiles.Add(archive.FilePath);
            UpdateWindowTitle();
            UpdatePageInfo();

            await ShowCurrentAsync();
        }
        catch (Exception ex)
        {
            if (_isClosing)
            {
                return;
            }

            HideLoadingOverlay();
            MessageBox.Show(this, ex.Message, "无法打开 TIS 文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_archive is null)
            {
                ShowHome();
            }
        }
    }

    private void ShowHome()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }

        _openGeneration++;
        CancelLoad();
        CancelPrefetch();
        CloseVideo();
        _archive?.Dispose();
        _archive = null;
        _index = -1;

        HomeView.Visibility = Visibility.Visible;
        ViewerView.Visibility = Visibility.Collapsed;
        ShowHomeChrome();
        Title = "TIS 相册";
        TitleText.Text = "TIS 相册";
        TitleText.ToolTip = null;
        InfoMeta.Text = string.Empty;
        HideError();
        HideLoadingOverlay();

        RestoreHomeBounds();
        RefreshRecent();
    }

    public void ActivateFromSecondInstance(string? path)
    {
        if (_isClosing)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = _isFullscreen ? WindowState.Maximized : WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            OpenFile(path);
        }
    }

    // ------------------------------------------------------------------
    //  Rendering the current item
    // ------------------------------------------------------------------

    private async Task ShowCurrentAsync()
    {
        if (_archive is null || CurrentItem is null)
        {
            return;
        }

        var archive = _archive;
        var item = CurrentItem!;
        var generation = ++_loadGeneration;

        CancelLoad();
        CancelPrefetch();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        HideError();
        ResetMedia();
        UpdateWindowTitle();
        UpdatePageInfo();
        InfoMeta.Text = item.KindLabel;
        MaximizeButton.Visibility = Visibility.Collapsed;

        if (item.Kind == TisMediaKind.Image)
        {
            await LoadImageAsync(archive, item, generation, token);
        }
        else if (item.Kind == TisMediaKind.Video)
        {
            await LoadVideoAsync(archive, item, generation, token);
        }
        else
        {
            ShowError("无法查看这个文件", "它不是受支持的图片或视频格式。可以尝试用系统默认应用打开。");
            FitWindowToContent(720, 480);
        }
    }

    private async Task LoadImageAsync(TisArchive archive, TisMediaItem item, int generation, CancellationToken token)
    {
        if (TryGetCachedImage(item.EntryKey, out var cached))
        {
            DisplayImage(cached, item);
            StartPrefetch(generation);
            return;
        }

        ShowLoadingOverlay("正在加载图片…");
        try
        {
            var bytes = await archive.ReadAllBytesAsync(item, token);
            var decoded = await Task.Run(() => ImageLoader.Decode(bytes, (int)ImageMaxDimension), token);

            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            AddImageToCache(item.EntryKey, decoded.Image);
            DisplayImage(decoded.Image, item);
            StartPrefetch(generation);
        }
        catch (OperationCanceledException)
        {
            // superseded by another navigation
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || _isClosing)
            {
                return;
            }

            ShowError(
                "无法显示这张图片",
                "系统可能缺少该格式的解码器，或者文件已损坏。\n\n" + ex.Message);
            FitWindowToContent(720, 480);
        }
    }

    private void DisplayImage(BitmapSource image, TisMediaItem item)
    {
        ImageHost.Source = image;
        ImageHost.Visibility = Visibility.Visible;
        _zoom = 1;
        _imageTranslate.X = 0;
        _imageTranslate.Y = 0;
        ApplyImageTransform();

        HideLoadingOverlay();
        InfoMeta.Text = $"{image.PixelWidth} × {image.PixelHeight}";
        FitWindowToContent(image.PixelWidth, image.PixelHeight);
        CacheCoverThumbnail(image);
    }

    private async Task LoadVideoAsync(TisArchive archive, TisMediaItem item, int generation, CancellationToken token)
    {
        ShowLoadingOverlay("正在准备视频…", showProgress: true);
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (generation != _loadGeneration)
                {
                    return;
                }

                LoadingProgress.Value = value * 100;
                LoadingText.Text = $"正在解压视频… {value:P0}";
            });

            var cachePath = await _cache.EnsureAsync(archive, item, progress, token);
            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            _currentVideoPath = cachePath;
            LoadingText.Text = "正在打开视频…";
            LoadingProgress.Visibility = Visibility.Collapsed;

            VideoHost.Source = new Uri(cachePath, UriKind.Absolute);
            VideoHost.Visibility = Visibility.Visible;
            VideoControls.Visibility = Visibility.Visible;
            BigPlayButton.Visibility = Visibility.Collapsed;

            VideoHost.Play();
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration || _isClosing)
            {
                return;
            }

            ShowError(
                "无法准备这个视频",
                "解压视频时出现问题，或者文件已损坏。\n\n" + ex.Message);
            FitWindowToContent(720, 480);
        }
    }

    private void ResetMedia()
    {
        CloseVideo();
        ImageHost.Source = null;
        ImageHost.Visibility = Visibility.Collapsed;
        _zoom = 1;
        _imageTranslate.X = 0;
        _imageTranslate.Y = 0;
        ApplyImageTransform();
        MediaStage.Cursor = Cursors.Arrow;
        InfoMeta.Text = string.Empty;
    }

    private void CloseVideo()
    {
        _videoTimer.Stop();
        _videoReady = false;
        _isPlaying = false;
        _isSeeking = false;

        try
        {
            VideoHost.Stop();
            VideoHost.Close();
            VideoHost.Source = null;
        }
        catch
        {
            // ignored
        }

        VideoHost.Visibility = Visibility.Collapsed;
        VideoControls.Visibility = Visibility.Collapsed;
        BigPlayButton.Visibility = Visibility.Collapsed;
        _currentVideoPath = null;
        _suppressSlider = true;
        PositionSlider.Value = 0;
        _suppressSlider = false;
        CurrentTimeText.Text = "00:00";
        TotalTimeText.Text = "00:00";
        UpdatePlayGlyph(false);
    }

    private void UpdateWindowTitle()
    {
        var item = CurrentItem;
        if (_archive is null || item is null)
        {
            Title = "TIS 相册";
            TitleText.Text = "TIS 相册";
            return;
        }

        var title = $"{_archive.FileName}-{item.DisplayName}";
        Title = title;
        TitleText.Text = title;
        TitleText.ToolTip = _archive.FilePath;
    }

    private void UpdatePageInfo()
    {
        if (_archive is null || CurrentItem is null)
        {
            InfoIndex.Text = "1 / 1";
            return;
        }

        InfoIndex.Text = $"{_index + 1} / {_archive.Items.Count}";

        var multiple = _archive.Items.Count > 1;
        PageBadge.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _archive.Items.Count - 1;
    }

    // ------------------------------------------------------------------
    //  Navigation
    // ------------------------------------------------------------------

    private void Navigate(int delta)
    {
        _ = NavigateAsync(delta);
    }

    private async Task NavigateAsync(int delta)
    {
        if (_archive is null)
        {
            return;
        }

        var next = _index + delta;
        if (next < 0)
        {
            ShowToast("已经是第一张");
            return;
        }

        if (next >= _archive.Items.Count)
        {
            ShowToast("已经是最后一张");
            return;
        }

        _index = next;
        await ShowCurrentAsync();
    }

    private async Task GoToAsync(int index)
    {
        if (_archive is null || index < 0 || index >= _archive.Items.Count || index == _index)
        {
            return;
        }

        _index = index;
        await ShowCurrentAsync();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHome();

    // ------------------------------------------------------------------
    //  Video
    // ------------------------------------------------------------------

    private void VideoHost_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoReady = true;
        HideLoadingOverlay();

        var duration = TimeSpan.Zero;
        if (VideoHost.NaturalDuration.HasTimeSpan)
        {
            duration = VideoHost.NaturalDuration.TimeSpan;
            _suppressSlider = true;
            PositionSlider.Maximum = Math.Max(duration.TotalSeconds, 0.1);
            _suppressSlider = false;
            TotalTimeText.Text = FormatTime(duration);
        }

        var width = VideoHost.NaturalVideoWidth;
        var height = VideoHost.NaturalVideoHeight;
        InfoMeta.Text = width > 0 && height > 0 ? $"{width} × {height}" : string.Empty;
        if (width > 0 && height > 0)
        {
            FitWindowToContent(width, height);
        }

        VideoHost.Volume = 0.85;
        VideoHost.IsMuted = _videoMuted;
        try
        {
            VideoHost.Play();
            _isPlaying = true;
        }
        catch
        {
            _isPlaying = false;
        }

        UpdatePlayGlyph(_isPlaying);
        BigPlayButton.Visibility = Visibility.Collapsed;
        VideoControls.Visibility = Visibility.Visible;
        _videoTimer.Start();
    }

    private void VideoHost_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        UpdatePlayGlyph(false);
        BigPlayButton.Visibility = Visibility.Visible;
        _videoTimer.Stop();
    }

    private void VideoHost_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _videoReady = false;
        _isPlaying = false;
        _videoTimer.Stop();
        ShowError(
            "无法播放这个视频",
            $"当前系统可能不支持 {CurrentItem?.Extension ?? "该"} 编码，或者文件已损坏。\n可以尝试用系统默认播放器打开。");
        FitWindowToContent(720, 480);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (!_videoReady)
        {
            return;
        }

        try
        {
            if (_isPlaying)
            {
                VideoHost.Pause();
                _isPlaying = false;
                BigPlayButton.Visibility = Visibility.Visible;
            }
            else
            {
                if (VideoHost.NaturalDuration.HasTimeSpan &&
                    VideoHost.Position >= VideoHost.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(250))
                {
                    VideoHost.Position = TimeSpan.Zero;
                }

                VideoHost.Play();
                _isPlaying = true;
                BigPlayButton.Visibility = Visibility.Collapsed;
            }

            UpdatePlayGlyph(_isPlaying);
        }
        catch
        {
            // ignored
        }
    }

    private void UpdatePlayGlyph(bool playing)
    {
        PlayPauseButton.Content = playing ? "\uE769" : "\uE768";
        PlayPauseButton.ToolTip = playing ? "暂停 (空格)" : "播放 (空格)";
        if (!playing && _videoReady)
        {
            BigPlayButton.Visibility = Visibility.Visible;
        }
        else if (playing)
        {
            BigPlayButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateVideoProgress()
    {
        if (!_videoReady)
        {
            return;
        }

        try
        {
            if (!_isSeeking)
            {
                _suppressSlider = true;
                PositionSlider.Value = Math.Clamp(VideoHost.Position.TotalSeconds, 0, PositionSlider.Maximum);
                _suppressSlider = false;
            }

            CurrentTimeText.Text = FormatTime(VideoHost.Position);
        }
        catch
        {
            // ignored
        }
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || !_videoReady)
        {
            return;
        }

        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        if (_isSeeking)
        {
            try
            {
                VideoHost.Position = TimeSpan.FromSeconds(e.NewValue);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_videoReady)
        {
            try
            {
                VideoHost.Position = TimeSpan.FromSeconds(PositionSlider.Value);
            }
            catch
            {
                // ignored
            }
        }

        _isSeeking = false;
    }

    private void SeekRelative(double seconds)
    {
        if (!_videoReady)
        {
            return;
        }

        try
        {
            var duration = VideoHost.NaturalDuration.HasTimeSpan ? VideoHost.NaturalDuration.TimeSpan : TimeSpan.Zero;
            var target = VideoHost.Position + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }

            if (duration > TimeSpan.Zero && target > duration)
            {
                target = duration;
            }

            VideoHost.Position = target;
            ShowToast(seconds > 0 ? "快进 5 秒" : "后退 5 秒");
        }
        catch
        {
            // ignored
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _videoMuted = !_videoMuted;
        VideoHost.IsMuted = _videoMuted;
        MuteButton.Content = _videoMuted ? "\uE74F" : "\uE767";
        ShowToast(_videoMuted ? "已静音" : "已取消静音");
        Focus();
    }

    private void ChangeVolume(double delta)
    {
        if (!_videoReady)
        {
            return;
        }

        VideoHost.Volume = Math.Clamp(VideoHost.Volume + delta, 0, 1);
        ShowToast($"音量 {VideoHost.Volume:P0}");
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    // ------------------------------------------------------------------
    //  Image zoom & pan
    // ------------------------------------------------------------------

    private void ApplyImageTransform()
    {
        _imageScale.ScaleX = _zoom;
        _imageScale.ScaleY = _zoom;
        MediaStage.Cursor = _zoom > 1.01 ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void MediaStage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CurrentItem?.Kind != TisMediaKind.Image || ImageHost.Source is null)
        {
            return;
        }

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var next = Math.Clamp(_zoom * factor, 1, 8);
        if (Math.Abs(next - _zoom) < 0.001)
        {
            return;
        }

        _zoom = next;
        if (_zoom <= 1.001)
        {
            _zoom = 1;
            _imageTranslate.X = 0;
            _imageTranslate.Y = 0;
        }

        ApplyImageTransform();
        e.Handled = true;
    }

    private void MediaStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CurrentItem?.Kind != TisMediaKind.Image)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _zoom = _zoom > 1.01 ? 1 : 2.2;
            _imageTranslate.X = 0;
            _imageTranslate.Y = 0;
            ApplyImageTransform();
            e.Handled = true;
            return;
        }

        if (_zoom <= 1.01)
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(MediaStage);
        _panOrigin = new Point(_imageTranslate.X, _imageTranslate.Y);
        MediaStage.CaptureMouse();
        e.Handled = true;
    }

    private void MediaStage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || CurrentItem?.Kind != TisMediaKind.Image)
        {
            return;
        }

        var point = e.GetPosition(MediaStage);
        var delta = point - _panStart;
        var maxX = MediaStage.ActualWidth * (_zoom - 1) / 2;
        var maxY = MediaStage.ActualHeight * (_zoom - 1) / 2;
        _imageTranslate.X = Math.Clamp(_panOrigin.X + delta.X, -maxX, maxX);
        _imageTranslate.Y = Math.Clamp(_panOrigin.Y + delta.Y, -maxY, maxY);
        e.Handled = true;
    }

    private void MediaStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        MediaStage.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    //  Window size / chrome
    // ------------------------------------------------------------------

    private void SetLoadingWindowSize()
    {
        WindowState = WindowState.Normal;
        Width = 620;
        Height = 460;
        CenterWindow();
    }

    private void FitWindowToContent(double contentWidth, double contentHeight)
    {
        if (_isFullscreen || !IsViewerVisible || contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(ViewerMinWidth, work.Width * 0.94);
        var maxHeight = Math.Max(ViewerMinHeight, work.Height * 0.94);
        var maxContentHeight = Math.Max(220, maxHeight - ToolbarHeight);

        var scale = Math.Min(1.0, Math.Min(maxWidth / contentWidth, maxContentHeight / contentHeight));
        var targetWidth = Math.Clamp(contentWidth * scale, ViewerMinWidth, maxWidth);
        var targetHeight = Math.Clamp(contentHeight * scale + ToolbarHeight, ViewerMinHeight, maxHeight);

        WindowState = WindowState.Normal;
        Width = targetWidth;
        Height = targetHeight;
        Left = work.Left + (work.Width - targetWidth) / 2;
        Top = work.Top + (work.Height - targetHeight) / 2;
        MaximizeButton.Visibility = Visibility.Collapsed;
    }

    private void CenterWindow()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void RestoreHomeBounds()
    {
        WindowState = WindowState.Normal;
        var work = SystemParameters.WorkArea;

        var width = _hasHomeBounds ? _homeBounds.Width : HomeWidth;
        var height = _hasHomeBounds ? _homeBounds.Height : HomeHeight;
        width = Math.Clamp(width, 840, Math.Max(840, work.Width));
        height = Math.Clamp(height, 520, Math.Max(520, work.Height));

        Width = width;
        Height = height;
        Left = _hasHomeBounds
            ? Math.Clamp(_homeBounds.Left, work.Left, Math.Max(work.Left, work.Right - width))
            : work.Left + (work.Width - width) / 2;
        Top = _hasHomeBounds
            ? Math.Clamp(_homeBounds.Top, work.Top, Math.Max(work.Top, work.Bottom - height))
            : work.Top + (work.Height - height) / 2;
    }

    private void EnterViewerChrome()
    {
        HomeView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        ViewerHeader.Visibility = Visibility.Visible;
        ViewerActions.Visibility = Visibility.Visible;
        MaximizeButton.Visibility = Visibility.Collapsed;
        MinWidth = ViewerMinWidth;
        MinHeight = ViewerMinHeight;
        TitleRow.Height = new GridLength(ToolbarHeight);
        TitleBar.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;

        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = ToolbarHeight;
        }
    }

    private void ShowHomeChrome()
    {
        ViewerHeader.Visibility = Visibility.Collapsed;
        ViewerActions.Visibility = Visibility.Collapsed;
        MaximizeButton.Visibility = Visibility.Visible;
        MinWidth = 840;
        MinHeight = 520;
        TitleRow.Height = new GridLength(ToolbarHeight);
        TitleBar.Visibility = Visibility.Visible;

        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = ToolbarHeight;
        }
    }

    private void Window_Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Window_Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (IsViewerVisible)
        {
            ToggleFullscreen();
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (IsViewerVisible && WindowState == WindowState.Maximized && !_isFullscreen)
        {
            EnterFullscreen();
            return;
        }

        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen || !IsViewerVisible)
        {
            return;
        }

        _isFullscreen = true;
        _restoreState = WindowState;
        _restoreTitleHeight = TitleRow.Height;
        _preFullscreenBounds = WindowState == WindowState.Maximized && RestoreBounds.Width > 0
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        TitleRow.Height = new GridLength(0);
        TitleBar.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Maximized;

        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = 0;
        }
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        _isFullscreen = false;
        TitleRow.Height = _restoreTitleHeight.Value > 0 ? _restoreTitleHeight : new GridLength(ToolbarHeight);
        TitleBar.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;

        if (_preFullscreenBounds.Width > 0)
        {
            Width = _preFullscreenBounds.Width;
            Height = _preFullscreenBounds.Height;
            Left = _preFullscreenBounds.Left;
            Top = _preFullscreenBounds.Top;
        }

        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = ToolbarHeight;
        }

        if (CurrentItem?.Kind == TisMediaKind.Image && ImageHost.Source is BitmapSource image)
        {
            FitWindowToContent(image.PixelWidth, image.PixelHeight);
        }
        else if (_videoReady && VideoHost.NaturalVideoWidth > 0)
        {
            FitWindowToContent(VideoHost.NaturalVideoWidth, VideoHost.NaturalVideoHeight);
        }
    }

    // ------------------------------------------------------------------
    //  Loading / error / toast
    // ------------------------------------------------------------------

    private void ShowLoadingOverlay(string text, bool showProgress = false)
    {
        LoadingText.Text = text;
        LoadingProgress.Value = 0;
        LoadingProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        ErrorOverlay.Visibility = Visibility.Collapsed;
    }

    private void HideLoadingOverlay() => LoadingOverlay.Visibility = Visibility.Collapsed;

    private void ShowError(string title, string detail)
    {
        HideLoadingOverlay();
        ErrorTitle.Text = title;
        ErrorDetail.Text = detail;
        ErrorOverlay.Visibility = Visibility.Visible;
        VideoControls.Visibility = Visibility.Collapsed;
        BigPlayButton.Visibility = Visibility.Collapsed;
    }

    private void HideError() => ErrorOverlay.Visibility = Visibility.Collapsed;

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async void ErrorOpenDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is null || CurrentItem is null)
        {
            return;
        }

        try
        {
            ShowToast("正在准备文件…");
            var archive = _archive;
            var item = CurrentItem;
            var path = await _cache.EnsureAsync(archive, item, null, CancellationToken.None);
            ShellIntegration.OpenWithDefaultApp(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开文件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ErrorReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is not null)
        {
            ShellIntegration.ShowInExplorer(_archive.FilePath);
        }
    }

    // ------------------------------------------------------------------
    //  Image cache / prefetch
    // ------------------------------------------------------------------

    private bool TryGetCachedImage(string key, out BitmapSource image)
    {
        if (_imageCache.TryGetValue(key, out image!))
        {
            TouchImageCache(key);
            return true;
        }

        return false;
    }

    private void AddImageToCache(string key, BitmapSource image)
    {
        if (_imageCache.ContainsKey(key))
        {
            TouchImageCache(key);
            return;
        }

        while (_imageCache.Count >= ImageCacheLimit && _imageCacheOrder.First is { } first)
        {
            _imageCache.Remove(first.Value);
            _imageCacheOrder.RemoveFirst();
        }

        _imageCache[key] = image;
        _imageCacheOrder.AddLast(key);
    }

    private void TouchImageCache(string key)
    {
        var node = _imageCacheOrder.Find(key);
        if (node is null)
        {
            return;
        }

        _imageCacheOrder.Remove(node);
        _imageCacheOrder.AddLast(node);
    }

    private void ClearImageCache()
    {
        _imageCache.Clear();
        _imageCacheOrder.Clear();
        _cachedArchivePath = null;
    }

    private void StartPrefetch(int generation)
    {
        CancelPrefetch();
        _prefetchCts = new CancellationTokenSource();
        _ = PrefetchNextAsync(generation, _prefetchCts.Token);
    }

    private async Task PrefetchNextAsync(int generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(140, token);
            if (generation != _loadGeneration || _archive is null)
            {
                return;
            }

            var next = _index + 1;
            if (next >= _archive.Items.Count)
            {
                return;
            }

            var item = _archive.Items[next];
            if (item.Kind != TisMediaKind.Image || _imageCache.ContainsKey(item.EntryKey))
            {
                return;
            }

            var archive = _archive;
            var bytes = await archive.ReadAllBytesAsync(item, token);
            var decoded = await Task.Run(() => ImageLoader.Decode(bytes, (int)ImageMaxDimension), token);

            if (token.IsCancellationRequested || generation != _loadGeneration)
            {
                return;
            }

            AddImageToCache(item.EntryKey, decoded.Image);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch
        {
            // prefetch is best effort
        }
    }

    private void CancelPrefetch()
    {
        try
        {
            _prefetchCts?.Cancel();
            _prefetchCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        _prefetchCts = null;
    }

    private void CacheCoverThumbnail(BitmapSource image)
    {
        if (_archive is null)
        {
            return;
        }

        var coverIndex = _archive.CoverIndex >= 0 ? _archive.CoverIndex : 0;
        if (_index != coverIndex)
        {
            return;
        }

        try
        {
            var scale = 320.0 / Math.Max(image.PixelWidth, image.PixelHeight);
            BitmapSource thumbnail = scale < 1
                ? new TransformedBitmap(image, new ScaleTransform(scale, scale))
                : image;
            if (thumbnail.CanFreeze)
            {
                thumbnail.Freeze();
            }

            _recentPreviews[_archive.FilePath] = new ArchivePreview(thumbnail, TisMediaKind.Image);
        }
        catch
        {
            // thumbnail cache is best effort
        }
    }

    // ------------------------------------------------------------------
    //  Home / recent files
    // ------------------------------------------------------------------

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 TIS 文件",
            Filter = "TIS 相册文件 (*.tis)|*.tis|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    private void NewArchive_Click(object sender, RoutedEventArgs e) => OpenCreateWindow(null);

    private void OpenCreateWindow(IEnumerable<string>? files)
    {
        var window = new CreateWindow(files) { Owner = this };
        window.ShowDialog();
        if (!string.IsNullOrWhiteSpace(window.CreatedArchivePath))
        {
            OpenFile(window.CreatedArchivePath);
        }
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        if (_recentCards.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(this, "要清空最近打开列表吗？（不会删除任何文件）", "TIS 相册",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        RecentFiles.Clear();
        _recentPreviews.Clear();
        RefreshRecent();
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            OpenFile(path);
        }
    }

    private void RefreshRecent()
    {
        var entries = RecentFiles.Load();
        _recentCards.Clear();

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry.Path);
            var folder = Path.GetDirectoryName(entry.Path) ?? string.Empty;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            {
                folder = "~" + folder[profile.Length..];
            }

            _recentCards.Add(new RecentCard
            {
                Path = entry.Path,
                Name = name,
                Folder = folder,
                When = FormatRelative(entry.OpenedAt),
            });
        }

        RecentCount.Text = $"{_recentCards.Count} 个文件";
        RecentEmpty.Visibility = _recentCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = _recentCards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _ = LoadRecentThumbnailsAsync(++_recentLoadVersion);
    }

    private async Task LoadRecentThumbnailsAsync(int version)
    {
        foreach (var card in _recentCards.ToList())
        {
            if (version != _recentLoadVersion)
            {
                return;
            }

            if (card.Thumbnail is not null)
            {
                continue;
            }

            if (!_recentPreviews.TryGetValue(card.Path, out var preview))
            {
                var path = card.Path;
                preview = await Task.Run(() => ArchiveThumbnailLoader.LoadPreview(path, 360));
                if (preview is not null)
                {
                    _recentPreviews[path] = preview;
                }
            }

            if (version != _recentLoadVersion)
            {
                return;
            }

            if (preview is not null)
            {
                card.Thumbnail = preview.Thumbnail;
                card.IsVideo = preview.Kind == TisMediaKind.Video;
                card.Glyph = preview.Kind == TisMediaKind.Video ? "\uE714" : "\uE8B9";
            }
        }
    }

    private static string FormatRelative(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today)
        {
            return $"今天 {local:HH:mm}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"昨天 {local:HH:mm}";
        }

        return local.Year == DateTimeOffset.Now.Year ? $"{local:M月d日}" : $"{local:yyyy/M/d}";
    }

    private void UpdateAssociationHint()
    {
        var registered = FileAssociation.IsRegistered();
        AssociationStatus.Text = registered
            ? "已关联 · 双击 .tis 直接打开"
            : "未关联 · 双击 .tis 不会自动打开";
        AssociationStatus.Foreground = registered
            ? new SolidColorBrush(Color.FromRgb(0x86, 0xC9, 0xA5))
            : new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x75));
    }

    private void ToggleAssociation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FileAssociation.IsRegistered())
            {
                var result = MessageBox.Show(this,
                    "已经关联了 .tis 文件。\n\n要取消关联吗？取消后双击 .tis 将不再自动打开本应用。",
                    "TIS 相册", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    FileAssociation.Unregister();
                    ShowToast("已取消 .tis 关联");
                }
            }
            else
            {
                FileAssociation.Register();
                ShowToast("已关联 .tis 文件，双击即可打开");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "设置文件关联失败：\n" + ex.Message, "TIS 相册", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateAssociationHint();
    }

    // ------------------------------------------------------------------
    //  Input events
    // ------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.O)
        {
            OpenFile_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.N)
        {
            OpenCreateWindow(null);
            e.Handled = true;
            return;
        }

        if (_archive is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                if (ctrl)
                {
                    SeekRelative(-5);
                }
                else
                {
                    Navigate(-1);
                }

                e.Handled = true;
                break;

            case Key.Right:
                if (ctrl)
                {
                    SeekRelative(5);
                }
                else
                {
                    Navigate(1);
                }

                e.Handled = true;
                break;

            case Key.Home:
                _ = GoToAsync(0);
                e.Handled = true;
                break;

            case Key.End:
                _ = GoToAsync(_archive.Items.Count - 1);
                e.Handled = true;
                break;

            case Key.Space:
                if (CurrentItem?.Kind == TisMediaKind.Video)
                {
                    TogglePlayPause();
                }
                else
                {
                    Navigate(1);
                }

                e.Handled = true;
                break;

            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case Key.Escape:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                    e.Handled = true;
                }

                break;

            case Key.M:
                Mute_Click(sender, e);
                e.Handled = true;
                break;

            case Key.Up:
                ChangeVolume(0.05);
                e.Handled = true;
                break;

            case Key.Down:
                ChangeVolume(-0.05);
                e.Handled = true;
                break;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Effects = files.Any(f =>
                Directory.Exists(f) ||
                f.EndsWith(".tis", StringComparison.OrdinalIgnoreCase) ||
                TisMediaTypes.IsSupportedMedia(f))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        var archive = files.FirstOrDefault(f => f.EndsWith(".tis", StringComparison.OrdinalIgnoreCase) && File.Exists(f));
        if (archive is not null)
        {
            OpenFile(archive);
            return;
        }

        var media = files.Where(f => Directory.Exists(f) || (File.Exists(f) && TisMediaTypes.IsSupportedMedia(f))).ToArray();
        if (media.Length > 0)
        {
            OpenCreateWindow(media);
        }
    }

    private void CancelLoad()
    {
        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        _loadCts = null;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _isClosing = true;
        CancelLoad();
        CancelPrefetch();
        CloseVideo();
        _archive?.Dispose();
        _archive = null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshRecent();
        UpdateAssociationHint();
        Activate();
        Focus();
    }

    public sealed class RecentCard : INotifyPropertyChanged
    {
        private BitmapSource? _thumbnail;
        private bool _isVideo;
        private string _glyph = "\uE8B9";

        public string Path { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Folder { get; init; } = string.Empty;

        public string When { get; init; } = string.Empty;

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

        public bool IsVideo
        {
            get => _isVideo;
            set
            {
                if (_isVideo == value)
                {
                    return;
                }

                _isVideo = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVideo)));
            }
        }

        public string Glyph
        {
            get => _glyph;
            set
            {
                if (_glyph == value)
                {
                    return;
                }

                _glyph = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
