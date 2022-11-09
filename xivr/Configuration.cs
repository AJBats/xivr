﻿using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace xivr
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public bool isEnabled { get; set; } = false;
        public bool isAutoEnabled { get; set; } = false;
        public bool forceFloatingScreen { get; set; } = false;
        public bool forceFloatingInCutscene { get; set; } = true;
        public bool horizontalLock { get; set; } = false;
        public bool verticalLock { get; set; } = false;
        public bool horizonLock { get; set; } = false;
        public bool runRecenter { get; set; } = false;
        public float offsetAmountX { get; set; } = 0.0f;
        public float offsetAmountY { get; set; } = 0.0f;
        public float snapRotateAmountX { get; set; } = 45.0f;
        public float snapRotateAmountY { get; set; } = 15.0f;
        public float uiOffsetZ { get; set; } = 0.0f;
        public float uiOffsetScale { get; set; } = 1.0f;


        [NonSerialized]
        private DalamudPluginInterface? pluginInterface;

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.pluginInterface!.SavePluginConfig(this);
        }
    }
}
