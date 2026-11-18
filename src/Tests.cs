using System;
using System.Linq;

namespace VpnWatch
{
    internal static class Tests
    {
        private static int Main(string[] args)
        {
            try
            {
                TestSchedule();
                Console.WriteLine("PASS: explicit start/stop, 2s polling during a long dial, retry ceiling, auth failure classification.");
                if (args.Contains("--probe"))
                {
                    Console.OutputEncoding = System.Text.Encoding.UTF8;
                    var profiles = Ras.Profiles();
                    Console.WriteLine("RAS enumeration: {0} VPN profiles", profiles.Count);
                    foreach (VpnProfile profile in profiles)
                    {
                        Console.WriteLine("{0}: state={1}", profile, Ras.State(profile));
                        Console.WriteLine("WindowsSavedPassword={0}", Ras.HasSavedPassword(profile));
                    }
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void TestSchedule()
        {
            var schedule = new RetrySchedule();
            Assert(!schedule.ShouldDial(LinkState.Down, false, 0), "No auto-start on application launch.");
            schedule.Start(0);
            Assert(schedule.ShouldDial(LinkState.Down, false, 0), "Arming a disconnected profile allows dial.");
            Assert(!schedule.ShouldDial(LinkState.Up, false, 2000), "An established tunnel is preserved.");
            for (long time = 2000; time < 120000; time += 2000)
            {
                Assert(!schedule.ShouldDial(LinkState.Connecting, false, time), "Wait for an external connection attempt.");
                Assert(!schedule.ShouldDial(LinkState.Down, true, time), "Do not start a second attempt while the first is pending.");
            }
            schedule.Failed(120000, 5, 60);
            Assert(!schedule.ShouldDial(LinkState.Down, false, 124000), "Wait for the configured retry delay.");
            Assert(schedule.ShouldDial(LinkState.Down, false, 126000), "Dial at the first poll after the delay.");
            schedule.Failed(130000, 5, 60);
            Assert(schedule.Due == 140000, "Retry delay increases after consecutive errors.");
            for (int i = 0; i < 10; i++) schedule.Failed(150000, 5, 17);
            Assert(schedule.Due == 167000, "Cap backoff at the configured maximum.");
            schedule.Reset(180000);
            schedule.Failed(180000, 3, 17);
            Assert(schedule.Due == 183000, "Recovering a connection resets the delay and uses current settings.");
            Assert(!schedule.ShouldDial(LinkState.Disconnecting, false, 185000), "Wait for Windows to release the previous tunnel.");
            schedule.Stop();
            Assert(!schedule.ShouldDial(LinkState.Down, false, 200000), "No retries after stop, even after deadline.");
            Assert(RetrySchedule.MustPause(691) && RetrySchedule.MustPause(RasFailure.NoSavedPassword)
                && RetrySchedule.MustPause(0x800B010F) && !RetrySchedule.MustPause(628), "Authentication and certificate errors must not be retried blindly.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
