using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VpnWatch
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool first;
            using (var instance = new Mutex(true, @"Local\VpnWatch.Desktop", out first))
            {
                if (!first)
                {
                    MessageBox.Show("Наблюдатель VPN уже запущен.", "Наблюдатель VPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WatchForm());
            }
        }
    }

    internal sealed class WatchSettings
    {
        private static readonly string FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnWatch", "timers.txt");
        public int Poll = 2, Connect = 120, Retry = 5, MaxRetry = 60;

        public static WatchSettings Load()
        {
            var settings = new WatchSettings();
            try
            {
                if (!File.Exists(FileName) || new FileInfo(FileName).Length > 256) return settings;
                string[] values = File.ReadAllLines(FileName);
                if (values.Length != 4) return settings;
                settings.Poll = Parse(values[0], 1, 60, 2);
                settings.Connect = Parse(values[1], 10, 600, 120);
                settings.Retry = Parse(values[2], 1, 300, 5);
                settings.MaxRetry = Parse(values[3], settings.Retry, 600, Math.Max(60, settings.Retry));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        private static int Parse(string value, int low, int high, int fallback)
        {
            int result;
            return Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= low && result <= high ? result : fallback;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName));
            string temporary = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temporary, new[] { Poll, Connect, Retry, MaxRetry }.Select(x => x.ToString(CultureInfo.InvariantCulture)));
                if (File.Exists(FileName)) File.Replace(temporary, FileName, null);
                else File.Move(temporary, FileName);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class WatchForm : Form
    {
        private readonly ComboBox profiles = new ComboBox();
        private readonly Button refresh = new Button(), connect = new Button(), disconnect = new Button(), watch = new Button();
        private readonly Label status = new Label(), detail = new Label();
        private readonly NumericUpDown poll = new NumericUpDown(), connectTimeout = new NumericUpDown();
        private readonly NumericUpDown retry = new NumericUpDown(), maxRetry = new NumericUpDown();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly RetrySchedule schedule = new RetrySchedule();
        private LinkState? link;
        private Ras.DialAttempt pending;
        private long deadline;
        private bool busy, closing, settingsChanged;
        private string message = "Выберите профиль Windows. Наблюдение включается вручную.";

        public WatchForm()
        {
            Text = "Наблюдатель VPN";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(610, 388);
            MinimumSize = new Size(580, 430);
            Font = new Font("Segoe UI", 10);
            BackColor = Color.White;
            Icon = SystemIcons.Application;

            var flow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(23, 17, 23, 16) };
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            flow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            Controls.Add(flow);

            var heading = new Label { Text = "Наблюдатель VPN", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 20, FontStyle.Bold), ForeColor = Color.FromArgb(25, 36, 52) };
            flow.Controls.Add(heading);
            var pick = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pick.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 111));
            pick.Controls.Add(new Label { Text = "VPN-профиль", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            profiles.DropDownStyle = ComboBoxStyle.DropDownList;
            profiles.Dock = DockStyle.Fill;
            profiles.AccessibleName = "VPN-профиль";
            profiles.Margin = new Padding(0, 8, 8, 0);
            pick.Controls.Add(profiles, 1, 0);
            SetupButton(refresh, "Обновить");
            pick.Controls.Add(refresh, 2, 0);
            flow.Controls.Add(pick);

            var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(239, 245, 252), ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 6, 10, 6), Margin = new Padding(0, 4, 0, 7) };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            status.Dock = DockStyle.Fill;
            status.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
            detail.Dock = DockStyle.Fill;
            detail.ForeColor = Color.FromArgb(42, 60, 85);
            card.Controls.Add(status);
            card.Controls.Add(detail);
            flow.Controls.Add(card);

            var settings = WatchSettings.Load();
            var group = new GroupBox { Text = "Интервалы, секунды", Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 4) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Padding = new Padding(8, 1, 8, 4) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            AddTimer(grid, "Проверять каждые", poll, 1, 60, settings.Poll, 0, 0);
            AddTimer(grid, "Ждать подключения", connectTimeout, 10, 600, settings.Connect, 2, 0);
            AddTimer(grid, "Пауза после ошибки", retry, 1, 300, settings.Retry, 0, 1);
            AddTimer(grid, "Максимальная пауза", maxRetry, 1, 600, settings.MaxRetry, 2, 1);
            maxRetry.Minimum = retry.Value;
            group.Controls.Add(grid);
            flow.Controls.Add(group);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            SetupButton(watch, "Включить наблюдение");
            watch.BackColor = Color.FromArgb(25, 92, 167);
            watch.ForeColor = Color.White;
            watch.FlatStyle = FlatStyle.Flat;
            watch.FlatAppearance.BorderSize = 0;
            SetupButton(connect, "Подключить");
            SetupButton(disconnect, "Отключить");
            buttons.Controls.Add(watch, 0, 0);
            buttons.Controls.Add(connect, 1, 0);
            buttons.Controls.Add(disconnect, 2, 0);
            flow.Controls.Add(buttons);
            flow.Controls.Add(new Label { Text = "Наблюдение работает, пока открыто это окно.", ForeColor = Color.FromArgb(76, 87, 106), Dock = DockStyle.Fill });

            refresh.Click += async delegate { await RefreshProfiles(); };
            profiles.SelectedIndexChanged += delegate { if (!busy) { link = null; Poll(); } };
            watch.Click += delegate { ToggleWatch(); };
            connect.Click += delegate { ConnectWithWindows(); };
            disconnect.Click += delegate { DisconnectSelected(); };
            poll.ValueChanged += delegate { timer.Interval = (int)poll.Value * 1000; settingsChanged = true; };
            connectTimeout.ValueChanged += delegate { settingsChanged = true; };
            retry.ValueChanged += delegate { maxRetry.Minimum = retry.Value; settingsChanged = true; };
            maxRetry.ValueChanged += delegate { settingsChanged = true; };
            timer.Interval = (int)poll.Value * 1000;
            timer.Tick += delegate { Poll(); };
            Shown += async delegate { await RefreshProfiles(); if (!closing) timer.Start(); };
            FormClosing += delegate { CloseSession(); };
            FormClosed += delegate { timer.Dispose(); };
            Render();
        }

        private static void SetupButton(Button button, string title)
        {
            button.Text = title; button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 5, 6, 5);
        }

        private static void AddTimer(TableLayoutPanel grid, string title, NumericUpDown control, int minimum, int maximum, int value, int x, int y)
        {
            control.Minimum = minimum; control.Maximum = maximum; control.Value = value;
            control.Margin = new Padding(0, 0, 7, 0);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.AccessibleName = title;
            grid.Controls.Add(new Label { Text = title, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, x, y);
            grid.Controls.Add(control, x + 1, y);
        }

        private VpnProfile Selected { get { return profiles.SelectedItem as VpnProfile; } }

        private async Task RefreshProfiles()
        {
            if (busy || schedule.Watching || pending != null) return;
            VpnProfile previous = Selected;
            busy = true;
            Render();
            try
            {
                List<VpnProfile> items = await Task.Run(() => Ras.Profiles());
                if (closing) return;
                profiles.Items.Clear();
                foreach (VpnProfile item in items) profiles.Items.Add(item);
                if (items.Count > 0)
                {
                    int index = previous == null ? -1 : items.FindIndex(p =>
                        (p.Id != Guid.Empty && p.Id == previous.Id) ||
                        (p.Name == previous.Name && p.Book == previous.Book));
                    profiles.SelectedIndex = index < 0 ? 0 : index;
                    message = "Подключите VPN или сразу включите наблюдение, если вход уже сохранён в Windows.";
                }
                else message = "Windows не нашла VPN-профилей. Добавьте профиль в параметрах Windows и обновите список.";
            }
            catch (Exception ex) { message = "Не удалось получить список профилей. " + ex.Message; }
            finally
            {
                busy = false;
                if (!closing) Poll();
            }
        }

        private void ToggleWatch()
        {
            if (Selected == null || busy) return;
            if (schedule.Watching) StopWatch("Наблюдение выключено. Подключённый туннель оставлен открытым.");
            else
            {
                schedule.Start(clock.ElapsedMilliseconds);
                message = "После обрыва приложение переподключит этот профиль.";
                Poll();
            }
            Render();
        }

        private void StopWatch(string reason)
        {
            schedule.Stop();
            message = reason;
            if (pending == null) return;
            Ras.DialAttempt attempt = pending;
            pending = null;
            try { attempt.Cancel(true); }
            catch (Exception ex) { message = reason + " Windows не подтвердила отмену попытки: " + ex.Message; }
        }

        private void ConnectWithWindows()
        {
            VpnProfile profile = Selected;
            if (busy || profile == null || link != LinkState.Down || pending != null) return;
            busy = true;
            Render();
            try { message = Ras.ConnectWithDialog(profile, Handle) ? "Windows подключила профиль." : "Подключение отменено."; }
            catch (Exception ex) { message = ex.Message; }
            finally { busy = false; Poll(); }
        }

        private void DisconnectSelected()
        {
            VpnProfile profile = Selected;
            if (busy || profile == null) return;
            StopWatch("Наблюдение выключено. Windows отключает выбранный VPN.");
            try { Ras.Disconnect(profile); }
            catch (Exception ex) { message = ex.Message; }
            Poll();
        }

        private void Poll()
        {
            VpnProfile profile = Selected;
            if (busy || closing) return;
            if (profile == null) { link = null; Render(); return; }
            long now = clock.ElapsedMilliseconds;
            try
            {
                link = Ras.State(profile);
                if (pending != null)
                {
                    long result = pending.Result;
                    if (result == 0 || (result < 0 && link == LinkState.Up))
                    {
                        pending.Forget(); pending = null;
                        schedule.Reset(now);
                        message = "VPN переподключён.";
                        return;
                    }
                    else if (result > 0 || now >= deadline)
                    {
                        Ras.DialAttempt attempt = pending;
                        pending = null;
                        try { attempt.Cancel(result < 0); }
                        catch (Exception ex)
                        {
                            StopWatch("Windows не подтвердила отмену попытки. " + ex.Message);
                            return;
                        }
                        uint code = result > 0 ? (uint)result : 1460;
                        RetryAfter(now, code, result > 0 ? Ras.ErrorText(code) : "Windows не завершила подключение за заданное время.");
                        return;
                    }
                    else return;
                }

                if (link == LinkState.Up) schedule.Reset(now);
                if (schedule.ShouldDial(link.Value, pending != null, now))
                {
                    try
                    {
                        pending = Ras.StartDial(profile);
                        deadline = clock.ElapsedMilliseconds + (long)connectTimeout.Value * 1000;
                        message = "Идёт переподключение. Повторная попытка дождётся результата Windows.";
                    }
                    catch (RasFailure ex) { RetryAfter(now, ex.Code, ex.Message); }
                }
            }
            catch (Exception ex)
            {
                link = null;
                message = "Не удалось проверить состояние VPN. " + ex.Message;
            }
            finally { Render(); }
        }

        private void RetryAfter(long now, uint code, string reason)
        {
            if (RetrySchedule.MustPause(code))
            {
                StopWatch("Наблюдение приостановлено. " + reason);
                return;
            }
            schedule.Failed(now, (int)retry.Value, (int)maxRetry.Value);
            message = reason;
        }

        private void Render()
        {
            bool selected = Selected != null;
            profiles.Enabled = !busy && !schedule.Watching && pending == null;
            refresh.Enabled = profiles.Enabled;
            watch.Enabled = selected && !busy;
            watch.Text = schedule.Watching ? "Остановить наблюдение" : "Включить наблюдение";
            connect.Enabled = !busy && selected && link == LinkState.Down && pending == null && !schedule.Watching;
            disconnect.Enabled = !busy && selected && (pending != null || (link.HasValue && link != LinkState.Down));
            string state = !selected || !link.HasValue ? "Состояние неизвестно" :
                link == LinkState.Up ? "Подключён" : pending != null || link == LinkState.Connecting ? "Подключается" :
                link == LinkState.Disconnecting ? "Отключается" : "Отключён";
            status.Text = state + (schedule.Watching ? "  ·  Наблюдение включено" : "  ·  Наблюдение выключено");
            status.ForeColor = link == LinkState.Up ? Color.FromArgb(18, 101, 66) : Color.FromArgb(25, 44, 73);
            long waitMs = schedule.Due - clock.ElapsedMilliseconds;
            detail.Text = message + (schedule.Watching && pending == null && link == LinkState.Down && waitMs > 0 ?
                " Следующая попытка через " + ((waitMs + 999) / 1000) + " с." : "");
        }

        private void CloseSession()
        {
            closing = true;
            timer.Stop();
            StopWatch("");
            if (!settingsChanged) return;
            try { new WatchSettings { Poll = (int)poll.Value, Connect = (int)connectTimeout.Value, Retry = (int)retry.Value, MaxRetry = (int)maxRetry.Value }.Save(); }
            catch (IOException ex) { MessageBox.Show(this, "Не удалось сохранить интервалы. " + ex.Message, Text); }
            catch (UnauthorizedAccessException ex) { MessageBox.Show(this, "Не удалось сохранить интервалы. " + ex.Message, Text); }
        }
    }
}
