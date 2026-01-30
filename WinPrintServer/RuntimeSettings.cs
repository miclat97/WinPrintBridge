namespace WinPrintBridge
{
    public class RuntimeSettings
    {
        public string PrinterName { get; set; } = "HP";
        public bool AutoCleanEnabled { get; set; } = false;
        public int AutoCleanTimeoutMinutes { get; set; } = 20;
        public bool PreviewEnabled { get; set; } = true;
    }
}
