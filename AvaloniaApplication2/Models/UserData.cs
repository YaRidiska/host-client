using System;

namespace AvaloniaApplication2.Models
{
    public class UserData
    {
        public string UserGuid { get; set; } = string.Empty;
        public string Subdomain { get; set; } = string.Empty;
        public bool IsSubdomainLocked { get; set; }
        public string SelectedVersion { get; set; } = "Fabric 1.20.1";
        public bool ShowServerLogs { get; set; } = false;
        public bool UseNoGui { get; set; } = true;
        public double ServerMemoryGB { get; set; } = 1.0;
    }
}