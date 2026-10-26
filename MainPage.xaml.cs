using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace ThrowTracerApp;

public partial class MainPage : ContentPage
{
    private enum RoundPhase
    {
        ReadyToStart,
        WaitingForCollect,
        Collecting
    }

    private readonly List<DiscThrow> _throws = new();
    private RoundPhase _phase = RoundPhase.ReadyToStart;
    private Location? _startLocation;
    private Location? _currentLocation;
    private PointF? _currentOffsetMeters;
    private bool _locationTimerRunning;
    private bool _isUpdatingLocation;
    private bool _locationErrorShown;

    private const float FieldWidthMeters = 100f;
    private const float FieldHeightMeters = 200f;
    private const float StartMarkerHalfSize = 7f;
    private const float BottomMargin = 20f;

    public MainPage()
    {
        InitializeComponent();
        UpdateUi();
    }

    private async void OnStartCollectClicked(object? sender, EventArgs e)
    {
        switch (_phase)
        {
            case RoundPhase.ReadyToStart:
                await StartRoundAsync();
                break;
            case RoundPhase.WaitingForCollect:
                await StartCollectingAsync();
                break;
        }
    }

    private void OnDoneClicked(object? sender, EventArgs e)
    {
        _phase = RoundPhase.ReadyToStart;
        _startLocation = null;
        _currentLocation = null;
        _currentOffsetMeters = null;
        _throws.Clear();
        UpdateUi();
        TracerCanvas.InvalidateSurface();
    }

    private void OnCanvasTouch(object? sender, SKTouchEventArgs e)
    {
        if (_phase != RoundPhase.Collecting || e.ActionType != SKTouchAction.Released || _currentOffsetMeters is null)
        {
            e.Handled = true;
            return;
        }

        CollectDisc();
        UpdateUi();
        TracerCanvas.InvalidateSurface();

        e.Handled = true;
    }

    private void CollectDisc()
    {
        if (_currentOffsetMeters is null)
        {
            return;
        }

        var offset = _currentOffsetMeters.Value;
        var distance = Math.Sqrt((offset.X * offset.X) + (offset.Y * offset.Y));

        _throws.Add(new DiscThrow(offset.X, offset.Y, distance));

        ShowToastAsync($"Throw recorded: {distance:F1} m");

    }

    private void OnCanvasPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;

        canvas.Clear(SKColors.DarkGreen);

        DrawStartMarker(canvas, info);
        DrawRecordedThrows(canvas, info);
        DrawCurrentPosition(canvas, info);
    }

    private async Task StartRoundAsync()
    {
        if (!await EnsureLocationPermissionAsync())
        {
            return;
        }

        _locationErrorShown = false;

        var location = await TryGetCurrentLocationAsync();
        if (location is null)
        {
            return;
        }

        _startLocation = location;
        _currentLocation = location;
        _currentOffsetMeters = new PointF(0f, 0f);
        _throws.Clear();
        _phase = RoundPhase.WaitingForCollect;

        UpdateUi();
        TracerCanvas.InvalidateSurface();
    }

    private async Task ShowToastAsync(string message)
    {
        var toast = Toast.Make(message, ToastDuration.Short, 14);
        await toast.Show();
    }

    private async Task StartCollectingAsync()
    {
        if (!await EnsureLocationPermissionAsync())
        {
            return;
        }

        _locationErrorShown = false;
        _phase = RoundPhase.Collecting;
        UpdateUi();

        StartLocationTimer();
        await UpdateCurrentLocationAsync();
    }

    private void StartLocationTimer()
    {
        if (_locationTimerRunning)
        {
            return;
        }

        _locationTimerRunning = true;

        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (_phase != RoundPhase.Collecting)
            {
                _locationTimerRunning = false;
                return false;
            }

            if (!_isUpdatingLocation)
            {
                _ = UpdateCurrentLocationAsync();
            }

            return true;
        });
    }

    private async Task UpdateCurrentLocationAsync()
    {
        if (_startLocation is null)
        {
            return;
        }

        _isUpdatingLocation = true;

        try
        {
            var location = await TryGetCurrentLocationAsync();
            if (location is null)
            {
                return;
            }

            _currentLocation = location;
            _currentOffsetMeters = ToOffsetMeters(_startLocation, location);

            UpdateUi();
            TracerCanvas.InvalidateSurface();
        }
        finally
        {
            _isUpdatingLocation = false;
        }
    }

    private async Task<bool> EnsureLocationPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (status == PermissionStatus.Granted)
        {
            return true;
        }

        await DisplayAlertAsync("Location required", "Location permission is needed to measure throw distances.", "OK");
        return false;
    }

    private async Task<Location?> TryGetCurrentLocationAsync()
    {
        try
        {
            var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10));
            return await Geolocation.Default.GetLocationAsync(request);
        }
        catch (FeatureNotSupportedException)
        {
            await ShowLocationErrorOnceAsync("Geolocation is not supported on this device.");
            return null;
        }
        catch (FeatureNotEnabledException)
        {
            await ShowLocationErrorOnceAsync("Enable location services to use this app.");
            return null;
        }
        catch (PermissionException)
        {
            await ShowLocationErrorOnceAsync("Location permission was denied.");
            return null;
        }
    }

    private async Task ShowLocationErrorOnceAsync(string message)
    {
        if (_locationErrorShown)
        {
            return;
        }

        _locationErrorShown = true;
        await DisplayAlertAsync("Location error", message, "OK");
    }

    private static PointF ToOffsetMeters(Location start, Location current)
    {
        const double EarthRadiusMeters = 6378137d;

        var startLatitudeRadians = DegreesToRadians(start.Latitude);
        var currentLatitudeRadians = DegreesToRadians(current.Latitude);
        var deltaLatitude = currentLatitudeRadians - startLatitudeRadians;
        var deltaLongitude = DegreesToRadians(current.Longitude - start.Longitude);
        var averageLatitude = (startLatitudeRadians + currentLatitudeRadians) / 2d;

        var northMeters = deltaLatitude * EarthRadiusMeters;
        var eastMeters = deltaLongitude * EarthRadiusMeters * Math.Cos(averageLatitude);

        return new PointF((float)eastMeters, (float)northMeters);
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);

    private static SKPoint GetStartPoint(SKImageInfo info) => new(info.Width / 2f, info.Height - BottomMargin);

    private static SKPoint ToScreenPoint(SKImageInfo info, PointF offsetMeters)
    {
        var startPoint = GetStartPoint(info);
        var x = startPoint.X + (offsetMeters.X / FieldWidthMeters) * info.Width;
        var y = startPoint.Y - (offsetMeters.Y / FieldHeightMeters) * info.Height;

        return new SKPoint(x, y);
    }

    private void DrawStartMarker(SKCanvas canvas, SKImageInfo info)
    {
        var startPoint = GetStartPoint(info);

        using var startPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        var markerRect = SKRect.Create(
            startPoint.X - StartMarkerHalfSize,
            startPoint.Y - StartMarkerHalfSize,
            StartMarkerHalfSize * 2,
            StartMarkerHalfSize * 2);

        canvas.DrawRect(markerRect, startPaint);
    }

    private void DrawRecordedThrows(SKCanvas canvas, SKImageInfo info)
    {
        using var throwPaint = new SKPaint
        {
            Color = SKColors.Orange,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var labelPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        using var labelFont = new SKFont
        {
            Size = 26
        };

        for (var i = 0; i < _throws.Count; i++)
        {
            var discThrow = _throws[i];
            var point = ToScreenPoint(info, new PointF((float)discThrow.EastMeters, (float)discThrow.NorthMeters));

            canvas.DrawCircle(point, 8, throwPaint);
            canvas.DrawText($"{i + 1}", point.X + 10, point.Y - 10, labelFont, labelPaint);
        }
    }

    private void DrawCurrentPosition(SKCanvas canvas, SKImageInfo info)
    {
        if (_currentOffsetMeters is null)
        {
            return;
        }

        var point = ToScreenPoint(info, _currentOffsetMeters.Value);
        var distance = Math.Sqrt((_currentOffsetMeters.Value.X * _currentOffsetMeters.Value.X) + (_currentOffsetMeters.Value.Y * _currentOffsetMeters.Value.Y));

        using var playerPaint = new SKPaint
        {
            Color = SKColors.DodgerBlue,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        using var textFont = new SKFont
        {
            Size = 30
        };

        canvas.DrawCircle(point, 10, playerPaint);
        canvas.DrawText($"{distance:F1} m", point.X + 14, point.Y - 14, textFont, textPaint);
    }

    private void UpdateUi()
    {
        StartCollectButton.Text = _phase switch
        {
            RoundPhase.ReadyToStart => "Start",
            RoundPhase.WaitingForCollect => "Collect",
            _ => "Collecting"
        };

        StartCollectButton.IsEnabled = _phase != RoundPhase.Collecting;
        DoneButton.IsEnabled = _phase != RoundPhase.ReadyToStart;

        var currentDistance = _currentOffsetMeters is null
            ? 0d
            : Math.Sqrt((_currentOffsetMeters.Value.X * _currentOffsetMeters.Value.X) + (_currentOffsetMeters.Value.Y * _currentOffsetMeters.Value.Y));

        CurrentDistanceLabel.Text = $"Distance: {currentDistance:F1} m";

        ThrowsLabel.Text = _throws.Count == 0
            ? "Throws recorded: 0"
            : $"Throws recorded: {_throws.Count} (last: {_throws[^1].DistanceMeters:F1} m)";
    }

    private readonly record struct DiscThrow(double EastMeters, double NorthMeters, double DistanceMeters);
}
