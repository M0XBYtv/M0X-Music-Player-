using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace M0XMusicPlayer
{
    public class CornerRadiusConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double width) return new CornerRadius(width / 2);
            return new CornerRadius(0);
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public partial class MainWindow : Window
    {
        private readonly MediaPlayer _mediaPlayer = new MediaPlayer();
        private readonly List<string> _playlistFiles = new List<string>();
        private int _currentTrackIndex = -1;
        private bool _isDraggingSlider = false;
        private bool _isShuffled = false;
        private readonly Random _random = new Random();

        private GlobalSystemMediaTransportControlsSessionManager _sessionManager;
        private GlobalSystemMediaTransportControlsSession _currentSession;

        private bool _isCompactMode = false;
        private double _normalWidth, _normalHeight;
        private readonly Geometry _playIcon = Geometry.Parse("M8,5.14V19.14L19,12.14L8,5.14Z");
        private readonly Geometry _pauseIcon = Geometry.Parse("M14,19H18V5H14M6,19H10V5H6V19Z");

        private readonly RotateTransform _normalVinylTransform = new RotateTransform();
        private readonly RotateTransform _compactVinylTransform = new RotateTransform();
        private readonly DoubleAnimation _spinAnimation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(5))) { RepeatBehavior = RepeatBehavior.Forever };

        public MainWindow()
        {
            InitializeComponent();
            SetupAnimation();
            SetupPlaybackTimer();
            SetupLocalPlayer();
            InitializeMediaSessionListener();
        }

        private void SetupAnimation()
        {
            NormalVinylCover.RenderTransform = _normalVinylTransform;
            CompactVinylCover.RenderTransform = _compactVinylTransform;
        }

        private void SetSpinning(bool isPlaying)
        {
            if (isPlaying)
            {
                _normalVinylTransform.BeginAnimation(RotateTransform.AngleProperty, _spinAnimation);
                _compactVinylTransform.BeginAnimation(RotateTransform.AngleProperty, _spinAnimation);
            }
            else
            {
                _normalVinylTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                _compactVinylTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        private void SetupPlaybackTimer()
        {
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += UpdateTimer_Tick;
            timer.Start();
        }

        private void SetupLocalPlayer()
        {
            _mediaPlayer.MediaEnded += LocalPlayer_MediaEnded;
            _mediaPlayer.MediaOpened += LocalPlayer_MediaOpened;
            VolumeSlider.Value = _mediaPlayer.Volume = 0.5;
        }

        #region System Media Integration

        private async void InitializeMediaSessionListener()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _sessionManager.CurrentSessionChanged += SystemMedia_CurrentSessionChanged;
                SystemMedia_CurrentSessionChanged(_sessionManager, null);
            }
            catch { /* Fails if OS version is too old. Ignore. */ }
        }

        private void SystemMedia_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            var session = sender.GetCurrentSession();
            Dispatcher.Invoke(() =>
            {
                if (session != null)
                {
                    _mediaPlayer.Pause();
                    if (_currentSession != null)
                    {
                        _currentSession.MediaPropertiesChanged -= SystemMedia_PropertiesChanged;
                        _currentSession.PlaybackInfoChanged -= SystemMedia_PlaybackInfoChanged;
                    }
                    _currentSession = session;
                    _currentSession.MediaPropertiesChanged += SystemMedia_PropertiesChanged;
                    _currentSession.PlaybackInfoChanged += SystemMedia_PlaybackInfoChanged;
                    UpdateDisplayFromSystemMedia(session);
                }
                else
                {
                    RestoreLocalPlayerDisplay();
                }
            });
        }

        private async void SystemMedia_PropertiesChanged(GlobalSystemMediaTransportControlsSession session, object args)
        {
            await Dispatcher.InvokeAsync(() => UpdateDisplayFromSystemMedia(session));
        }

        private async void SystemMedia_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession session, object args)
        {
            await Dispatcher.InvokeAsync(() => UpdateDisplayFromSystemMedia(session));
        }

        private async void UpdateDisplayFromSystemMedia(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null) return;
            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();
            if (mediaProperties == null || playbackInfo == null) return;

            TitleText.Text = mediaProperties.Title ?? "Unknown Title";
            ArtistAlbumText.Text = $"{mediaProperties.Artist} - {mediaProperties.AlbumTitle}".Trim(' ', '-', ',');
            if (string.IsNullOrWhiteSpace(ArtistAlbumText.Text)) ArtistAlbumText.Text = "Unknown Artist/Album";

            PlayPauseButton.IsEnabled = true;
            PlayPauseIcon.Data = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? _pauseIcon : _playIcon;
            NextButton.IsEnabled = playbackInfo.Controls.IsNextEnabled;
            PreviousButton.IsEnabled = playbackInfo.Controls.IsPreviousEnabled;
            ShuffleButton.IsEnabled = false;
            PositionSlider.IsEnabled = playbackInfo.Controls.IsPlaybackPositionEnabled;

            SetSpinning(playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

            if (mediaProperties.Thumbnail != null)
            {
                var bitmap = await GetBitmapFromStreamReference(mediaProperties.Thumbnail);
                AlbumArtBrush.ImageSource = bitmap;
                AlbumArtBrushCompact.ImageSource = bitmap;
                BackgroundImage.Source = bitmap;
            }
            else
            {
                AlbumArtBrush.ImageSource = null;
                AlbumArtBrushCompact.ImageSource = null;
                BackgroundImage.Source = null;
            }
        }

        private async Task<BitmapImage> GetBitmapFromStreamReference(IRandomAccessStreamReference thumb)
        {
            var stream = await thumb.OpenReadAsync();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream.AsStream();
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void RestoreLocalPlayerDisplay()
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= SystemMedia_PropertiesChanged;
                _currentSession.PlaybackInfoChanged -= SystemMedia_PlaybackInfoChanged;
                _currentSession = null;
            }
            SetSpinning(false);
            SetLocalPlayerControlsEnabled(true);
            if (_currentTrackIndex != -1)
            {
                LoadMetadataForLocalTrack();
                PlayPauseIcon.Data = _mediaPlayer.CanPause ? _pauseIcon : _playIcon;
                SetSpinning(_mediaPlayer.CanPause);
            }
            else
            {
                TitleText.Text = "M0X Music Player";
                ArtistAlbumText.Text = "Drag & Drop Music to Start";
                AlbumArtBrush.ImageSource = null;
                AlbumArtBrushCompact.ImageSource = null;
                BackgroundImage.Source = null;
                PlayPauseIcon.Data = _playIcon;
                CurrentTimeText.Text = "0:00";
                TotalTimeText.Text = "0:00";
                PositionSlider.Value = 0;
            }
        }

        private void SetLocalPlayerControlsEnabled(bool isEnabled)
        {
            PositionSlider.IsEnabled = isEnabled;
            PreviousButton.IsEnabled = isEnabled;
            NextButton.IsEnabled = isEnabled;
            ShuffleButton.IsEnabled = isEnabled;
            PlayPauseButton.IsEnabled = true;
        }

        #endregion

        #region Local Player Logic

        private void PlayLocalTrack(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= _playlistFiles.Count || _currentSession != null) return;
            _currentTrackIndex = trackIndex;
            Playlist.SelectedIndex = _currentTrackIndex;
            _mediaPlayer.Open(new Uri(_playlistFiles[_currentTrackIndex]));
            _mediaPlayer.Play();
        }

        private void LoadMetadataForLocalTrack()
        {
            if (_currentTrackIndex < 0) return;
            try
            {
                var file = TagLib.File.Create(_playlistFiles[_currentTrackIndex]);
                TitleText.Text = string.IsNullOrWhiteSpace(file.Tag.Title) ? Path.GetFileNameWithoutExtension(file.Name) : file.Tag.Title;
                ArtistAlbumText.Text = $"{string.Join(", ", file.Tag.Performers)} - {file.Tag.Album}".Trim(' ', '-', ',');
                if (string.IsNullOrWhiteSpace(ArtistAlbumText.Text)) ArtistAlbumText.Text = "Unknown Artist/Album";
                if (file.Tag.Pictures.Length > 0)
                {
                    var memoryStream = new MemoryStream(file.Tag.Pictures[0].Data.Data);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = memoryStream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    AlbumArtBrush.ImageSource = bitmap;
                    AlbumArtBrushCompact.ImageSource = bitmap;
                    BackgroundImage.Source = bitmap;
                }
                else
                {
                    AlbumArtBrush.ImageSource = null;
                    AlbumArtBrushCompact.ImageSource = null;
                    BackgroundImage.Source = null;
                }
            }
            catch (Exception ex)
            {
                TitleText.Text = Path.GetFileNameWithoutExtension(_playlistFiles[_currentTrackIndex]);
                ArtistAlbumText.Text = "Could not read metadata";
                Console.WriteLine($"Metadata Error: {ex.Message}");
            }
        }

        private void UpdatePlaylistDisplay()
        {
            Playlist.Items.Clear();
            foreach (var file in _playlistFiles)
            {
                Playlist.Items.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        #endregion

        #region UI Event Handlers

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null)
            {
                var playbackInfo = _currentSession.GetPlaybackInfo();
                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    await _currentSession.TryPauseAsync();
                else
                    await _currentSession.TryPlayAsync();
            }
            else
            {
                if (_mediaPlayer.Source == null && _playlistFiles.Any()) PlayLocalTrack(0);
                else if (_mediaPlayer.CanPause)
                {
                    _mediaPlayer.Pause();
                    SetSpinning(false);
                }
                else
                {
                    _mediaPlayer.Play();
                    SetSpinning(true);
                }
            }
        }

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null) await _currentSession.TrySkipPreviousAsync();
            else if (_playlistFiles.Count > 0)
            {
                int prevIndex = _currentTrackIndex - 1;
                if (prevIndex < 0) prevIndex = _playlistFiles.Count - 1;
                PlayLocalTrack(prevIndex);
            }
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null) await _currentSession.TrySkipNextAsync();
            else PlayNextLocalTrack();
        }

        private void PlayNextLocalTrack()
        {
            if (_playlistFiles.Count > 0)
            {
                if (_isShuffled) PlayLocalTrack(_random.Next(0, _playlistFiles.Count));
                else PlayLocalTrack((_currentTrackIndex + 1) % _playlistFiles.Count);
            }
        }

        private void LoadFilesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Audio Files|*.mp3;*.wav;*.flac;*.aac;*.ogg;*.wma|All Files|*.*"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                _playlistFiles.AddRange(openFileDialog.FileNames);
                UpdatePlaylistDisplay();
                if (_mediaPlayer.Source == null) PlayLocalTrack(0);
            }
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            _isShuffled = !_isShuffled;
            var button = sender as Button;
            if (button != null) button.ToolTip = _isShuffled ? "Disable Shuffle" : "Enable Shuffle";
            button.Foreground = _isShuffled ? new SolidColorBrush(Colors.DeepSkyBlue) : new SolidColorBrush(Colors.White);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _mediaPlayer.Volume = VolumeSlider.Value;
        }

        private async void PositionSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isDraggingSlider = false;
            var slider = sender as Slider;
            var newPosition = TimeSpan.FromSeconds(slider.Value);
            if (_currentSession != null)
            {
                if (_currentSession.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled)
                    await _currentSession.TryChangePlaybackPositionAsync(newPosition.Ticks);
            }
            else
            {
                _mediaPlayer.Position = newPosition;
            }
        }

        private void PositionSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void Playlist_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Playlist.SelectedIndex != -1) PlayLocalTrack(Playlist.SelectedIndex);
        }

        #endregion

        #region Player Events

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            if (_isDraggingSlider) return;
            if (_currentSession != null)
            {
                var timeline = _currentSession.GetTimelineProperties();
                if (timeline != null)
                {
                    PositionSlider.Maximum = timeline.EndTime.TotalSeconds;
                    PositionSlider.Value = timeline.Position.TotalSeconds;
                    CurrentTimeText.Text = timeline.Position.ToString(@"m\:ss");
                    TotalTimeText.Text = timeline.EndTime.ToString(@"m\:ss");
                }
            }
            else if (_mediaPlayer.Source != null && _mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                PositionSlider.Value = _mediaPlayer.Position.TotalSeconds;
                CurrentTimeText.Text = _mediaPlayer.Position.ToString(@"m\:ss");
            }
        }

        private void LocalPlayer_MediaOpened(object sender, EventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                PositionSlider.Maximum = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TotalTimeText.Text = _mediaPlayer.NaturalDuration.TimeSpan.ToString(@"m\:ss");
            }
            PlayPauseIcon.Data = _pauseIcon;
            PlayPauseButton.ToolTip = "Pause";
            LoadMetadataForLocalTrack();
            SetSpinning(true);
        }

        private void LocalPlayer_MediaEnded(object sender, EventArgs e)
        {
            SetSpinning(false);
            PlayNextLocalTrack();
        }

        #endregion

        #region Window Management

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            _isCompactMode = !_isCompactMode;
            Topmost = _isCompactMode;

            if (_isCompactMode)
            {
                _normalWidth = Width;
                _normalHeight = Height;
                NormalViewGrid.Visibility = Visibility.Collapsed;
                CompactViewGrid.Visibility = Visibility.Visible;
                ResizeMode = ResizeMode.CanResize;
                MinHeight = 150; MaxHeight = 250;
                MinWidth = 150; MaxWidth = 250;
                Width = 150; Height = 150;
                BackgroundImage.Visibility = Visibility.Collapsed;
            }
            else
            {
                CompactViewGrid.Visibility = Visibility.Collapsed;
                NormalViewGrid.Visibility = Visibility.Visible;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                MinHeight = 500; MaxHeight = double.PositiveInfinity;
                MinWidth = 450; MaxWidth = double.PositiveInfinity;
                Width = _normalWidth;
                Height = _normalHeight;
                BackgroundImage.Visibility = Visibility.Visible;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = ((string[])e.Data.GetData(DataFormats.FileDrop)).SelectMany(path =>
                {
                    if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories);
                    return new[] { path };
                }).Where(f => new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma" }.Contains(Path.GetExtension(f).ToLowerInvariant()));

                if (files.Any())
                {
                    _playlistFiles.AddRange(files);
                    UpdatePlaylistDisplay();
                    if (_mediaPlayer.Source == null || _mediaPlayer.Position == TimeSpan.Zero) PlayLocalTrack(0);
                }
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCompactMode) return;
            WindowState = (WindowState == WindowState.Normal) ? WindowState.Maximized : WindowState.Normal;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)));
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(200)));
        }

        #endregion
    }
}
