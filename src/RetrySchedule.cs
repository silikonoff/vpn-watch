using System;

namespace VpnWatch
{
    internal sealed class RetrySchedule
    {
        public bool Watching { get; private set; }
        public long Due { get; private set; }
        private int failures;

        public void Start(long now) { Watching = true; Reset(now); }
        public void Stop() { Watching = false; }
        public void Reset(long now) { failures = 0; Due = now; }

        public bool ShouldDial(LinkState state, bool attemptInProgress, long now)
        {
            return Watching && state == LinkState.Down && !attemptInProgress && now >= Due;
        }

        public void Failed(long now, int retrySeconds, int maxRetrySeconds)
        {
            int exponent = Math.Min(failures, 15);
            failures = Math.Min(failures + 1, 15);
            long delay = Math.Min(Math.Max(retrySeconds, maxRetrySeconds), (long)retrySeconds << exponent);
            Due = now + delay * 1000;
        }

        public static bool MustPause(uint code)
        {
            switch (code)
            {
                case RasFailure.NoSavedPassword:
                case 5: case 623: case 631: case 645: case 646: case 647: case 648: case 649:
                case 691: case 709: case 775: case 812: case 1223:
                case 0x80090322: case 0x80090325: case 0x80090328:
                case 0x800B0101: case 0x800B0109: case 0x800B010F:
                    return true;
                default:
                    return false;
            }
        }
    }
}
