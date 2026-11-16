using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>
/// 오래 걸리는 일을 다른 스레드에서 돌리며 진행 막대 · 남은 시간 · [취소]를 보이는 작은 대화상자.
/// 0.3초 안에 끝나는 일은 창을 보이지 않는다 (깜빡이지 않게: 그동안은 투명).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class ProgressForm : Form
{
    private readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous };
    private readonly Label message = new Label(), detail = new Label();
    private readonly Button cancelButton = new Button();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
    private readonly DateTime started = DateTime.UtcNow;
    /// <summary>진행 (일하는 스레드가 쓴다). Form은 참조로 마샬링되므로 필드를 따로 둔다 (Volatile에 ref로 넘기려고).</summary>
    private sealed class Shared { public double Fraction; }
    private readonly Shared shared = new Shared();
    private volatile bool cancelled;
    private Task work;

    private ProgressForm(string title, string text)
    {
        Text = title;
        AppIcon.Apply(this);
        Font = ShellTheme.Ui;
        ClientSize = new Size(420, 132);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ControlBox = false;
        Opacity = 0;   // 0.3초 넘게 걸릴 때만 보인다

        message.SetBounds(16, 14, 388, 22);
        message.Text = text;
        message.AutoEllipsis = true;
        bar.SetBounds(16, 42, 388, 20);
        detail.SetBounds(16, 68, 388, 20);
        detail.ForeColor = ShellTheme.Muted;
        cancelButton.Text = "취소";
        cancelButton.SetBounds(324, 94, 80, 28);
        cancelButton.Click += delegate { cancelled = true; cancelButton.Enabled = false; detail.Text = "취소하는 중…"; };
        CancelButton = cancelButton;
        Controls.AddRange(new Control[] { message, bar, detail, cancelButton });

        timer.Tick += delegate
        {
            double el = (DateTime.UtcNow - started).TotalSeconds;
            if (Opacity < 1 && el >= 0.3) Opacity = 1;
            double f = Math.Max(0, Math.Min(1, Volatile.Read(ref shared.Fraction)));
            bar.Value = (int)Math.Round(f * 1000);
            if (work != null && work.IsCompleted) { Close(); return; }   // 끝났거나 취소를 받고 그만뒀으면
            if (cancelled) return;
            string rest = f > 0.02 ? " · 남은 시간 약 " + Clock(el / f - el) : "";
            detail.Text = Math.Round(f * 100) + "% · " + Clock(el) + " 지남" + rest;
        };
    }

    private static string Clock(double s)
    {
        int t = (int)Math.Max(0, Math.Round(s));
        return t >= 60 ? t / 60 + "분 " + t % 60 + "초" : t + "초";
    }

    /// <summary>
    /// job(진행 알림 0~1, 취소 여부)를 다른 스레드에서 돌리고 끝날 때까지 기다린다 (창은 모달).
    /// 취소했으면 cancelled = true (job은 취소를 보고 스스로 그만둔다). job의 예외는 여기서 다시 던진다.
    /// </summary>
    public static T Run<T>(IWin32Window owner, string title, string text, Func<Action<double>, Func<bool>, T> job, out bool cancelled)
    {
        using (var f = new ProgressForm(title, text))
        {
            T result = default(T);
            f.Shown += delegate
            {
                f.work = Task.Run(() => { result = job(x => Volatile.Write(ref f.shared.Fraction, x), () => f.cancelled); });
                f.timer.Start();
            };
            f.ShowDialog(owner);
            f.timer.Stop();
            try { f.work?.Wait(); }
            catch (AggregateException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); }
            cancelled = f.cancelled;
            return result;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
