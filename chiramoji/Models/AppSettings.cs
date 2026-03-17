using System;

namespace Chiramoji.Models
{
    public class AppSettings
    {
        public string? LastComPort { get; set; }
        public int BaudRate { get; set; } = 115200;
        public bool AutoConnect { get; set; } = true;
        
        // Display Engine
        public float FontSize { get; set; } = 20;
        public byte Brightness { get; set; } = 255;
        public string FontFamily { get; set; } = "Yu Gothic";
        
        // Keylogger
        public bool ResetOnClick { get; set; } = true;
        public bool ClearOnEnter { get; set; } = false;

        // UI Visibility
        public bool ShowConnectivity { get; set; } = true;
        public bool ShowDirectControl { get; set; } = true;
        public bool ShowInputMode { get; set; } = true;
        public bool ShowPreview { get; set; } = true;
        public bool ShowTelemetry { get; set; } = true;
        public bool ShowLogs { get; set; } = true;
        public bool ShowImageSection { get; set; } = true;

        // App behavior
        public bool StartWithWindows { get; set; } = true;
        public bool CloseButtonMinimizesToTray { get; set; } = true;

        // Image restore
        public string? SelectedImagePath { get; set; }
        public bool RestoreImageModeApplied { get; set; } = false;
    }
}


