using System;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Cached GUIStyles for consistent CE inspector appearance.
    /// Styles are created lazily and cached to avoid per-frame allocation.
    /// </summary>
    public static class CEStyleCache
    {
        // ═══════════════════════════════════════════════════════════════
        // ICONS
        // ═══════════════════════════════════════════════════════════════

        private static Texture2D _diamondIcon;
        private static Texture2D _checkIcon;

        /// <summary>
        /// CE diamond icon (brand logo).
        /// </summary>
        public static Texture2D DiamondIcon
        {
            get
            {
                if (_diamondIcon == null)
                    _diamondIcon = CreateDiamondIcon();
                return _diamondIcon;
            }
        }

        /// <summary>
        /// Checkmark icon for optimization status.
        /// </summary>
        public static Texture2D CheckIcon
        {
            get
            {
                if (_checkIcon == null)
                    _checkIcon = CreateCheckIcon();
                return _checkIcon;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // HEADER STYLES
        // ═══════════════════════════════════════════════════════════════

        private static GUIStyle _headerStyle;
        private static GUIStyle _headerTextStyle;
        private static GUIStyle _badgeStyle;

        public static GUIStyle HeaderStyle
        {
            get
            {
                if (_headerStyle == null)
                {
                    _headerStyle = new GUIStyle(EditorStyles.toolbar)
                    {
                        fixedHeight = 24,
                        padding = new RectOffset(8, 8, 4, 4),
                        margin = new RectOffset(0, 0, 0, 2)
                    };
                    _headerStyle.normal.background = MakeTex(1, 1, CEColors.HeaderBg);
                }
                return _headerStyle;
            }
        }

        public static GUIStyle HeaderTextStyle
        {
            get
            {
                if (_headerTextStyle == null)
                {
                    _headerTextStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 12,
                        alignment = TextAnchor.MiddleLeft
                    };
                    _headerTextStyle.normal.textColor = CEColors.TextPrimary;
                }
                return _headerTextStyle;
            }
        }

        public static GUIStyle BadgeStyle
        {
            get
            {
                if (_badgeStyle == null)
                {
                    _badgeStyle = new GUIStyle(EditorStyles.miniButton)
                    {
                        fontSize = 9,
                        fontStyle = FontStyle.Bold,
                        padding = new RectOffset(5, 5, 2, 2),
                        margin = new RectOffset(2, 2, 4, 4),
                        fixedHeight = 16
                    };
                }
                return _badgeStyle;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // STATUS BAR STYLES
        // ═══════════════════════════════════════════════════════════════

        private static GUIStyle _statusBarStyle;
        private static GUIStyle _statusTextStyle;
        private static GUIStyle _statusPositiveStyle;
        private static GUIStyle _statusSeparatorStyle;

        public static GUIStyle StatusBarStyle
        {
            get
            {
                if (_statusBarStyle == null)
                {
                    _statusBarStyle = new GUIStyle(EditorStyles.helpBox)
                    {
                        fixedHeight = 20,
                        padding = new RectOffset(8, 8, 2, 2),
                        margin = new RectOffset(0, 0, 0, 4)
                    };
                }
                return _statusBarStyle;
            }
        }

        public static GUIStyle StatusTextStyle
        {
            get
            {
                if (_statusTextStyle == null)
                {
                    _statusTextStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        fontSize = 10
                    };
                    _statusTextStyle.normal.textColor = CEColors.TextSecondary;
                }
                return _statusTextStyle;
            }
        }

        public static GUIStyle StatusPositiveStyle
        {
            get
            {
                if (_statusPositiveStyle == null)
                {
                    _statusPositiveStyle = new GUIStyle(StatusTextStyle);
                    _statusPositiveStyle.normal.textColor = CEColors.TextPositive;
                }
                return _statusPositiveStyle;
            }
        }

        public static GUIStyle StatusSeparatorStyle
        {
            get
            {
                if (_statusSeparatorStyle == null)
                {
                    _statusSeparatorStyle = new GUIStyle(StatusTextStyle)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fixedWidth = 16
                    };
                }
                return _statusSeparatorStyle;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // GROUP STYLES
        // ═══════════════════════════════════════════════════════════════

        private static GUIStyle _groupHeaderStyle;
        private static GUIStyle _groupCountStyle;
        private static GUIStyle _groupBoxStyle;
        private static GUIStyle _foldoutStyle;

        public static GUIStyle GroupHeaderStyle
        {
            get
            {
                if (_groupHeaderStyle == null)
                {
                    _groupHeaderStyle = new GUIStyle()
                    {
                        padding = new RectOffset(0, 0, 2, 2)
                    };
                }
                return _groupHeaderStyle;
            }
        }

        public static GUIStyle GroupCountStyle
        {
            get
            {
                if (_groupCountStyle == null)
                {
                    _groupCountStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight
                    };
                    _groupCountStyle.normal.textColor = CEColors.TextSecondary;
                }
                return _groupCountStyle;
            }
        }

        public static GUIStyle GroupBoxStyle
        {
            get
            {
                if (_groupBoxStyle == null)
                {
                    _groupBoxStyle = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(4, 4, 4, 4),
                        margin = new RectOffset(0, 0, 2, 4)
                    };
                }
                return _groupBoxStyle;
            }
        }

        public static GUIStyle FoldoutStyle
        {
            get
            {
                if (_foldoutStyle == null)
                {
                    _foldoutStyle = new GUIStyle(EditorStyles.foldout)
                    {
                        fontStyle = FontStyle.Bold
                    };
                }
                return _foldoutStyle;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // OPTIMIZATION PANEL STYLES
        // ═══════════════════════════════════════════════════════════════

        private static GUIStyle _optimizationPanelStyle;
        private static GUIStyle _optimizationHeaderStyle;
        private static GUIStyle _optimizationCheckStyle;
        private static GUIStyle _optimizationNameStyle;
        private static GUIStyle _optimizationValueStyle;

        public static GUIStyle OptimizationPanelStyle
        {
            get
            {
                if (_optimizationPanelStyle == null)
                {
                    _optimizationPanelStyle = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(8, 8, 6, 6),
                        margin = new RectOffset(0, 0, 4, 4)
                    };
                }
                return _optimizationPanelStyle;
            }
        }

        public static GUIStyle OptimizationHeaderStyle
        {
            get
            {
                if (_optimizationHeaderStyle == null)
                {
                    _optimizationHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 11
                    };
                }
                return _optimizationHeaderStyle;
            }
        }

        public static GUIStyle OptimizationCheckStyle
        {
            get
            {
                if (_optimizationCheckStyle == null)
                {
                    _optimizationCheckStyle = new GUIStyle(EditorStyles.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 12
                    };
                    _optimizationCheckStyle.normal.textColor = CEColors.TextPositive;
                }
                return _optimizationCheckStyle;
            }
        }

        public static GUIStyle OptimizationNameStyle
        {
            get
            {
                if (_optimizationNameStyle == null)
                {
                    _optimizationNameStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontSize = 11
                    };
                }
                return _optimizationNameStyle;
            }
        }

        public static GUIStyle OptimizationValueStyle
        {
            get
            {
                if (_optimizationValueStyle == null)
                {
                    _optimizationValueStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontSize = 11
                    };
                    _optimizationValueStyle.normal.textColor = CEColors.TextSecondary;
                }
                return _optimizationValueStyle;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // UTILITY METHODS
        // ═══════════════════════════════════════════════════════════════

        private static bool _initialized;

        /// <summary>
        /// Initialize all styles. Called by CEInspectorBootstrap on domain reload.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Force initialization of all lazy properties
            // Using explicit access to trigger lazy initialization
            var icon = DiamondIcon;
            var h = HeaderStyle;
            var s = StatusBarStyle;
            var g = GroupHeaderStyle;
            var o = OptimizationPanelStyle;
        }

        /// <summary>
        /// Clear all cached styles. Useful for skin changes.
        /// </summary>
        public static void ClearCache()
        {
            _initialized = false;
            
            _diamondIcon = null;
            _checkIcon = null;
            
            _headerStyle = null;
            _headerTextStyle = null;
            _badgeStyle = null;
            
            _statusBarStyle = null;
            _statusTextStyle = null;
            _statusPositiveStyle = null;
            _statusSeparatorStyle = null;
            
            _groupHeaderStyle = null;
            _groupCountStyle = null;
            _groupBoxStyle = null;
            _foldoutStyle = null;
            
            _optimizationPanelStyle = null;
            _optimizationHeaderStyle = null;
            _optimizationCheckStyle = null;
            _optimizationNameStyle = null;
            _optimizationValueStyle = null;
        }

        /// <summary>
        /// Create a solid color texture.
        /// </summary>
        private static Texture2D MakeTex(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;

            var tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        /// <summary>
        /// Create the CE diamond icon programmatically.
        /// </summary>
        private static Texture2D CreateDiamondIcon()
        {
            const int size = 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];

            float cx = size / 2f;
            float cy = size / 2f;
            float radius = size / 2f - 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Diamond shape using Manhattan distance
                    float dx = Mathf.Abs(x - cx) / radius;
                    float dy = Mathf.Abs(y - cy) / radius;
                    float dist = dx + dy;

                    if (dist <= 1f)
                    {
                        // Inner gradient for depth effect
                        float alpha = 1f - dist * 0.3f;
                        pixels[y * size + x] = new Color(
                            CEColors.CEAccent.r,
                            CEColors.CEAccent.g,
                            CEColors.CEAccent.b,
                            alpha
                        );
                    }
                    else
                    {
                        pixels[y * size + x] = Color.clear;
                    }
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        /// <summary>
        /// Create a checkmark icon programmatically.
        /// </summary>
        private static Texture2D CreateCheckIcon()
        {
            const int size = 12;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];

            // Initialize with transparent
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.clear;

            // Draw a simple checkmark
            Color checkColor = CEColors.TextPositive;
            
            // Short stroke (bottom-left to middle-bottom)
            SetPixelSafe(pixels, size, 2, 5, checkColor);
            SetPixelSafe(pixels, size, 3, 6, checkColor);
            SetPixelSafe(pixels, size, 4, 7, checkColor);
            
            // Long stroke (middle-bottom to top-right)
            SetPixelSafe(pixels, size, 5, 6, checkColor);
            SetPixelSafe(pixels, size, 6, 5, checkColor);
            SetPixelSafe(pixels, size, 7, 4, checkColor);
            SetPixelSafe(pixels, size, 8, 3, checkColor);
            SetPixelSafe(pixels, size, 9, 2, checkColor);

            // Thicken the lines
            SetPixelSafe(pixels, size, 2, 6, checkColor);
            SetPixelSafe(pixels, size, 3, 7, checkColor);
            SetPixelSafe(pixels, size, 4, 8, checkColor);
            SetPixelSafe(pixels, size, 5, 7, checkColor);
            SetPixelSafe(pixels, size, 6, 6, checkColor);
            SetPixelSafe(pixels, size, 7, 5, checkColor);
            SetPixelSafe(pixels, size, 8, 4, checkColor);
            SetPixelSafe(pixels, size, 9, 3, checkColor);

            tex.SetPixels(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Point;
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static void SetPixelSafe(Color[] pixels, int size, int x, int y, Color color)
        {
            if (x >= 0 && x < size && y >= 0 && y < size)
                pixels[y * size + x] = color;
        }
    }
}

