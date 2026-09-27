using System;

namespace TrenchHQ.Core.Panels
{
    internal sealed class OverlayDefinition
    {
        public bool UseApplicationWindow { get; set; }
        public double HeightDip { get; set; } = 480;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Overlay";
        public bool Enabled { get; set; } = true;
        public string[] WidgetIds { get; set; } = [];
        public double WidthDip { get; set; } = 350;
        public string BackgroundColor { get; set; } = "#000000";
        public string TextColor { get; set; } = "#F5F5F5";
        public string ContentSize { get; set; } = "standard";
        public double LuminosityOpacity { get; set; } = 85;
        public double TintOpacity { get; set; } = 40;
        public bool PriceFlashOnChange { get; set; } = true;
        public string Shortcut { get; set; } = string.Empty;
        public int? PositionX { get; set; }
        public int? PositionY { get; set; }
        public string MonitorDeviceName { get; set; } = string.Empty;
    }

    internal sealed class DesktopSetup
    {
        public int Version { get; set; } = 7;
        public OverlayDefinition[] Overlays { get; set; } = [];
        public DockedBarDefinition[] DockedBars { get; set; } = [];
    }

    internal sealed class DockedBarDefinition
    {
        public bool UseApplicationWindow { get; set; }
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Panel Top";
        public bool Enabled { get; set; } = true;
        public string[] WidgetIds { get; set; } = [];
        public string MonitorDeviceName { get; set; } = string.Empty;
        public string Edge { get; set; } = "Top";
        public int ThicknessPx { get; set; } = 50;
        public string BackgroundColor { get; set; } = "#000000";
        public string TextColor { get; set; } = "#F5F5F5";
        public string ContentSize { get; set; } = "standard";
        public bool PriceFlashOnChange { get; set; } = true;
        public string Shortcut { get; set; } = string.Empty;
    }
}
