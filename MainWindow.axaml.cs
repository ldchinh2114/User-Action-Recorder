using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using SharpHook;
using SharpHook.Native;
using System;
using System.Diagnostics;

namespace CopyUserAction;

public partial class MainWindow : Window
{
    private bool _isRecording = false;
    private readonly RecorderService _recorderService = new();
    private List<UserAction> _recordedActions = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _uiStopwatch = new();
    private CancellationTokenSource? _replayCts;
    private int _totalReplays = 0;
    private TaskPoolGlobalHook? _stopHook;
    private KeyCode _stopKey = KeyCode.VcP;

    public MainWindow()
    {
        InitializeComponent();
        this.Closed += (s, e) =>
        {
            _replayCts?.Cancel();
            _recorderService.Dispose();
            _stopHook?.Dispose();
        };

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _uiTimer.Tick += OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var txtTimer = this.FindControl<TextBlock>("TxtTimer");
        if (txtTimer != null)
        {
            txtTimer.Text = _uiStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        }
    }

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        _isRecording = !_isRecording;
        var btn = (Button)sender!;

        if (_isRecording)
        {
            btn.Content = "Stop";
            _uiStopwatch.Restart();
            _uiTimer.Start();
            _recorderService.Start();
        }
        else
        {
            btn.Content = "Record";
            _recordedActions = _recorderService.Stop();
            _uiStopwatch.Stop();
            _uiTimer.Stop();
        }
    }

    private async void OnReplayClick(object? sender, RoutedEventArgs e)
    {
        if (_replayCts != null)
        {
            _replayCts.Cancel();
            return;
        }

        if (_recordedActions.Count == 0)
        {
            await ShowMessage("No actions recorded to replay.");
            return;
        }

        var btn = (Button)sender!;
        var btnRecord = this.FindControl<Button>("BtnRecord");
        var chkInfinite = this.FindControl<CheckBox>("ChkInfinite");
        var numIterations = this.FindControl<NumericUpDown>("NumIterations");
        var txtReplayCount = this.FindControl<TextBlock>("TxtReplayCount");
        var txtHotkey = this.FindControl<TextBox>("TxtHotkey");

        // Parse Hotkey
        if (txtHotkey != null && !string.IsNullOrWhiteSpace(txtHotkey.Text))
        {
            _stopKey = ParseKey(txtHotkey.Text.Trim());
        }

        _replayCts = new CancellationTokenSource();
        var token = _replayCts.Token;

        btn.Content = "Stop Replay";
        if (btnRecord != null) btnRecord.IsEnabled = false;

        bool isInfinite = chkInfinite?.IsChecked ?? false;
        int maxIterations = (int)(numIterations?.Value ?? 1);
        int currentIteration = 0;

        // Initialize Stop Hook
        _stopHook = new TaskPoolGlobalHook();
        _stopHook.KeyPressed += OnStopKeyPressed;
        _ = _stopHook.RunAsync();

        _uiTimer.Start();

        try
        {
            while (!token.IsCancellationRequested && (isInfinite || currentIteration < maxIterations))
            {
                currentIteration++;
                _uiStopwatch.Restart();

                await Task.Run(async () =>
                {
                    var simulator = new EventSimulator();
                    long firstTimestamp = _recordedActions[0].Timestamp;
                    var replayStopwatch = Stopwatch.StartNew();

                    foreach (var action in _recordedActions)
                    {
                        if (token.IsCancellationRequested) break;

                        long targetTime = action.Timestamp - firstTimestamp;
                        long waitTime = targetTime - replayStopwatch.ElapsedMilliseconds;

                        if (waitTime > 0)
                        {
                            try
                            {
                                await Task.Delay((int)waitTime, token);
                            }
                            catch (TaskCanceledException)
                            {
                                break;
                            }
                        }

                        switch (action.Type)
                        {
                            case ActionType.MouseMove:
                                simulator.SimulateMouseMovement(action.X, action.Y);
                                break;
                            case ActionType.MouseDown:
                                simulator.SimulateMousePress(action.MouseButton);
                                break;
                            case ActionType.MouseUp:
                                simulator.SimulateMouseRelease(action.MouseButton);
                                break;
                            case ActionType.MouseWheel:
                                simulator.SimulateMouseWheel(action.WheelDelta, MouseWheelScrollDirection.Vertical, MouseWheelScrollType.UnitScroll);
                                break;
                            case ActionType.KeyDown:
                                simulator.SimulateKeyPress(action.KeyCode);
                                break;
                            case ActionType.KeyUp:
                                simulator.SimulateKeyRelease(action.KeyCode);
                                break;
                        }
                    }
                }, token);

                if (!token.IsCancellationRequested)
                {
                    _totalReplays++;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (txtReplayCount != null)
                            txtReplayCount.Text = $"Total Replays: {_totalReplays}";
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Cleanup Stop Hook
            if (_stopHook != null)
            {
                _stopHook.KeyPressed -= OnStopKeyPressed;
                _stopHook.Dispose();
                _stopHook = null;
            }

            var oldCts = _replayCts;
            _replayCts = null;
            oldCts?.Dispose();

            _uiStopwatch.Stop();
            _uiTimer.Stop();

            btn.IsEnabled = true;
            if (btnRecord != null) btnRecord.IsEnabled = true;
            btn.Content = "Replay";
        }
    }

    private void OnStopKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode == _stopKey && _replayCts != null && !_replayCts.IsCancellationRequested)
        {
            _replayCts.Cancel();
        }
    }

    private KeyCode ParseKey(string input)
    {
        // Simple heuristic: try to find matching Enum value
        // User might type "P", "F1", "Esc"

        // 1. Try Direct match (rare for Vc prefixes)
        if (Enum.TryParse<KeyCode>(input, true, out var result)) return result;

        // 2. Try prepending "Vc" (e.g. "P" -> "VcP")
        if (Enum.TryParse<KeyCode>("Vc" + input, true, out result)) return result;

        // 3. Common mappings
        if (input.Equals("Esc", StringComparison.OrdinalIgnoreCase)) return KeyCode.VcEscape;
        if (input.Equals("Enter", StringComparison.OrdinalIgnoreCase)) return KeyCode.VcEnter;
        if (input.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) return KeyCode.VcLeftControl;
        if (input.Equals("Alt", StringComparison.OrdinalIgnoreCase)) return KeyCode.VcLeftAlt;
        if (input.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return KeyCode.VcLeftShift;

        // Default to P if unknown
        return KeyCode.VcP;
    }

    private void OnTopMostCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            Topmost = checkBox.IsChecked ?? false;
        }
    }

    private async void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        await ShowMessage("User Action Recorder Help:\n\n1. Click Record to start capturing actions.\n2. Click Stop to finish.\n3. Click Replay to play back.\n4. Set Stop Hotkey (default P) to interrupt replay.");
    }

    private Task ShowMessage(string message)
    {
        var dialog = new Window
        {
            Title = "Information",
            Content = new TextBlock { Text = message, Margin = new Avalonia.Thickness(20) },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog(this);
    }
}
