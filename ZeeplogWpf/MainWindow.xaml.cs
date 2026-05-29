using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ZeeplogWpf.Services;

namespace ZeeplogWpf;

public partial class MainWindow : Window
{
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    public MainWindow() => InitializeComponent();

    // ── File management ──────────────────────────────────────────────────────

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "운동 기록 파일 선택",
            Filter = "운동 기록 파일 (*.fit;*.gpx;*.tcx)|*.fit;*.gpx;*.tcx|모든 파일 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        AddFiles(dlg.FileNames);
    }

    private void BtnClearFiles_Click(object sender, RoutedEventArgs e)
    {
        LbFiles.Items.Clear();
        UpdateDropHint();
    }

    private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            LbFiles.Items.Remove(path);
            UpdateDropHint();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddFiles(files);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".fit", ".gpx", ".tcx" };
        var existing = LbFiles.Items.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            if (!allowed.Contains(Path.GetExtension(p))) continue;
            if (existing.Add(p))
                LbFiles.Items.Add(p);
        }
        UpdateDropHint();
    }

    private void UpdateDropHint()
        => TbDropHint.Visibility = LbFiles.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ── Conversion ───────────────────────────────────────────────────────────

    private async void BtnConvert_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _cts?.Cancel();
            return;
        }

        var files = LbFiles.Items.Cast<string>().ToList();
        if (files.Count == 0) { AppendLog("파일을 먼저 선택하세요."); return; }

        bool doCsv   = RbCsv.IsChecked   == true || RbBoth.IsChecked == true;
        bool doVideo = RbVideo.IsChecked  == true || RbBoth.IsChecked == true;
        if (!int.TryParse(TbFps.Text, out int fps) || fps < 1) fps = 60;

        _cts = new CancellationTokenSource();
        _isRunning = true;
        BtnConvert.Content = "취소";
        TbLog.Text = "";
        SetProgress(0, "변환 시작...");

        // CSV-only tasks don't need frame progress; each file maps to 1 step.
        // Video tasks have per-frame progress reported separately.
        int totalTasks = files.Count * ((doCsv ? 1 : 0) + (doVideo ? 1 : 0));
        int doneTasks  = 0;

        var logProg   = new Progress<string>(msg => AppendLog(msg));
        var frameProg = new Progress<(int Cur, int Total)>(p =>
        {
            double pct = doneTasks * 100.0 / totalTasks + p.Cur * 100.0 / (p.Total * totalTasks);
            SetProgress((int)pct, $"프레임 {p.Cur}/{p.Total}");
        });

        try
        {
            await Task.Run(() =>
            {
                foreach (var file in files)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(file);
                    string ext  = Path.GetExtension(file).ToLowerInvariant();

                    if (doCsv)
                    {
                        if (ext != ".fit")
                        {
                            ((IProgress<string>)logProg).Report($"[CSV 건너뜀] {name} — FIT 파일만 지원");
                        }
                        else
                        {
                            CsvExporter.Export(file, logProg);
                        }
                        doneTasks++;
                        Dispatcher.InvokeAsync(() => SetProgress(doneTasks * 100 / totalTasks, $"{doneTasks}/{totalTasks} 완료"));
                    }

                    if (doVideo)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        VideoGenerator.Generate(file, fps, logProg, frameProg, _cts.Token);
                        doneTasks++;
                        Dispatcher.InvokeAsync(() => SetProgress(doneTasks * 100 / totalTasks, $"{doneTasks}/{totalTasks} 완료"));
                    }
                }
            }, _cts.Token);

            AppendLog("──────────────────────────");
            AppendLog("모든 변환이 완료되었습니다!");
            SetProgress(100, "완료");
        }
        catch (OperationCanceledException)
        {
            AppendLog("변환이 취소되었습니다.");
            SetProgress(0, "취소됨");
        }
        catch (Exception ex)
        {
            AppendLog($"오류: {ex.Message}");
            SetProgress(0, "오류 발생");
        }
        finally
        {
            _isRunning = false;
            BtnConvert.Content = "변환 시작";
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void AppendLog(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TbLog.Text += message + "\n";
            SvLog.ScrollToBottom();
        });
    }

    private void SetProgress(int pct, string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            PbProgress.Value = Math.Clamp(pct, 0, 100);
            TbStatus.Text    = status;
        });
    }
}
