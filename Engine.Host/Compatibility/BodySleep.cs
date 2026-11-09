namespace UnityEngine
{
    public enum RigidbodySleepMode2D { NeverSleep, StartAwake, StartAsleep }
    public sealed partial class Rigidbody2D
    {
        public RigidbodySleepMode2D sleepMode { get; set; } = RigidbodySleepMode2D.StartAwake;
    }
}
