using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

public partial class AgentWorld3DControl : UserControl
{
    private const double WorldLimitX = 8.1;
    private const double WorldLimitZ = 5.7;
    private const double BubblePointerGap = 14;
    private const double BubbleTailHeight = 8;
    private const double BubbleTailWidth = 18;
    private const double AgentCollisionRadius = 0.46;
    private const double MiniMapLeft = 6;
    private const double MiniMapTop = 16;
    private const double MiniMapWidth = 156;
    private const double MiniMapHeight = 84;
    private const double HudSideMargin = 16;
    private const double MaxAnimationStepSeconds = 0.12;
    private const int MaterialCacheCapacity = 512;
    private static readonly object BoxMeshCacheLock = new();
    private static readonly Dictionary<BoxMeshKey, MeshGeometry3D> BoxMeshCache = [];
    private static readonly object RoundMeshCacheLock = new();
    private static readonly Dictionary<SphereMeshKey, MeshGeometry3D> SphereMeshCache = [];
    private static readonly Dictionary<CylinderMeshKey, MeshGeometry3D> CylinderMeshCache = [];
    private static readonly object MaterialCacheLock = new();
    private static readonly Dictionary<MaterialKey, MaterialCacheEntry> MaterialCache = [];
    private static readonly LinkedList<MaterialKey> MaterialCacheUsage = [];
    private readonly DispatcherTimer animationTimer;
    private readonly Stopwatch animationClock = new();
    private readonly Func<bool> animationsEnabledProvider;
    private readonly bool observeSystemMotionPreferences;
    private readonly Model3DGroup sceneGroup = new();
    private readonly List<WorldAgentVisual> agentVisuals = [];
    private readonly Dictionary<string, Ellipse> miniMapMarkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MiniMapMarkerRenderState> miniMapMarkerStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeMiniMapAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> inactiveMiniMapMarkerIds = [];
    private readonly Border miniMapCameraTargetMarker = new()
    {
        Width = 14,
        Height = 14,
        BorderThickness = new Thickness(1),
        Background = Brushes.Transparent,
        IsHitTestVisible = false
    };
    private readonly Rectangle miniMapBounds = new()
    {
        Width = 156,
        Height = 84,
        StrokeThickness = 1,
        Fill = Brushes.Transparent,
        IsHitTestVisible = false
    };
    private readonly Line miniMapVerticalAxis = new()
    {
        X1 = 84,
        Y1 = 16,
        X2 = 84,
        Y2 = 100,
        StrokeThickness = 1,
        IsHitTestVisible = false
    };
    private readonly Line miniMapHorizontalAxis = new()
    {
        X1 = 6,
        Y1 = 58,
        X2 = 162,
        Y2 = 58,
        StrokeThickness = 1,
        IsHitTestVisible = false
    };
    private readonly PerspectiveCamera camera;
    private Point? lastMousePoint;
    private Point? dragStartPoint;
    private CameraDragMode dragMode = CameraDragMode.None;
    private AgentWorldCameraMode cameraMode = AgentWorldCameraMode.FollowSpeaker;
    private Point3D cameraTarget = new(0, 0.72, 0);
    private Vector3D manualPan = new(0, 0, 0);
    private double cameraYaw;
    private double cameraPitch = 0.56;
    private double cameraDistance = 12.2;
    private double preOverviewCameraPitch = 0.56;
    private double preOverviewCameraDistance = 12.2;
    private Vector3D preOverviewManualPan = new(0, 0, 0);
    private bool hasPreOverviewCamera;
    private string selectedAgentId = "";
    private bool manualSelectionPinned;
    private bool inspectorDismissed;
    private bool cinematicAutoCamera;
    private bool dragMoved;
    private AgentWorldSnapshot? currentWorld;
    private double elapsedSeconds;
    private double lastAnimateElapsed;
    private Point[] animationPositions = [];
    private Point[] animationNextPositions = [];
    private double[] animationPhases = [];
    private int animationBufferResizeCount;
    private bool systemMotionPreferenceSubscribed;
    private bool miniMapRosterDirty = true;
    private bool miniMapStyleDirty = true;
    private string worldPulseSummary = "";
    private string worldBadgeLabel = "";
    private int snapshotApplyCount;
    // Content+theme signature of the last rendered world; identical re-applies skip the
    // full scene teardown/rebuild and let the running animation carry on.
    private string lastWorldSignature = "";
    private int sceneRebuildCount;
    private static readonly Color NeutralStageLightColor = Color.FromRgb(116, 136, 214);
    // Reused across scene rebuilds so its colour can ease toward the speaker's accent each frame.
    private readonly DirectionalLight speakerAccentLight = new(NeutralStageLightColor, new Vector3D(-0.28, -0.42, 0.88));

    // Static obstacles (center X, center Z, radius) the agents navigate around. Built with
    // the scene geometry so prop layout and collision can never drift apart.
    private static readonly (double X, double Z, double Radius)[] WorldObstacles =
    [
        (0, 0, 0.95),      // central arena console / dais
        (-5.6, -2.4, 0.5), // data pylon
        (5.6, 2.4, 0.5),   // data pylon
        (-5.6, 2.4, 0.5),  // data pylon
        (5.6, -2.4, 0.5)   // data pylon
    ];

    internal int DebugAvatarVisualCount => agentVisuals.Count;

    internal int DebugSnapshotApplyCount => snapshotApplyCount;

    internal bool DebugIsAnimationRunning => animationTimer.IsEnabled;

    internal bool DebugReducedMotion => !animationsEnabledProvider();

    internal int DebugAnimationBufferResizeCount => animationBufferResizeCount;

    internal int DebugAnimationBufferCapacity => animationPositions.Length;

    internal int DebugLegendItemCount => WorldLegendItems.Children.Count;

    internal Point3D DebugCameraTarget => cameraTarget;

    internal double DebugCameraYaw => cameraYaw;

    internal double DebugCameraDistance => cameraDistance;

    internal double DebugCameraPitch => cameraPitch;

    internal string DebugCameraMode => cameraMode.ToString();

    internal string DebugSelectedAgentId => selectedAgentId;

    internal bool DebugCinematicAutoCamera => cinematicAutoCamera;

    internal bool DebugInspectorVisible => AgentInspectorPanel.Visibility == Visibility.Visible;

    internal bool DebugEmptyStateVisible => EmptyStatePanel.Visibility == Visibility.Visible;

    internal bool DebugMiniMapVisible => WorldMiniMapPanel.Visibility == Visibility.Visible;

    internal bool DebugLegendVisible => WorldLegendPanel.Visibility == Visibility.Visible;

    internal double DebugInspectorPanelWidth => AgentInspectorPanel.Width;

    internal double DebugWorldControlPanelMaxWidth => WorldControlPanel.MaxWidth;

    internal Thickness DebugInspectorPanelMargin => AgentInspectorPanel.Margin;

    internal Rect DebugWorldHeaderTextBounds => ElementBounds(WorldHeaderTextPanel);

    internal Rect DebugWorldBadgeBounds => ElementBounds(WorldBadge);

    internal double DebugWorldBadgeMaxWidth => WorldBadge.MaxWidth;

    internal string DebugWorldBadgeText => WorldBadgeText.Text;

    internal string DebugWorldStatusText => WorldStatusText.Text;

    internal AgentWorldPulse? DebugWorldPulse => currentWorld?.Pulse;

    internal string DebugWorldPulseSummary => currentWorld is null ? "" : WorldPulseSummary(currentWorld.Pulse);

    internal bool DebugWorldCuePanelVisible => WorldCuePanel.Visibility == Visibility.Visible;

    internal int DebugWorldCueCount => WorldCueItems.Children.Count;

    internal double DebugWorldCuePanelMaxWidth => WorldCuePanel.MaxWidth;

    internal IReadOnlyList<string> DebugWorldCueTexts => WorldCueItems.Children
        .OfType<DependencyObject>()
        .Select(TextContent)
        .ToArray();

    internal IReadOnlyList<string> DebugWorldCueAutomationNames => WorldCueItems.Children
        .OfType<UIElement>()
        .Select(AutomationProperties.GetName)
        .ToArray();

    internal IReadOnlyList<string> DebugLegendTexts => WorldLegendItems.Children
        .OfType<DependencyObject>()
        .Select(TextContent)
        .ToArray();

    internal string DebugFollowCameraAutomationName => AutomationProperties.GetName(FollowCameraButton);

    internal string DebugFollowCameraAutomationHelpText => AutomationProperties.GetHelpText(FollowCameraButton);

    internal string DebugOverviewCameraAutomationName => AutomationProperties.GetName(OverviewCameraButton);

    internal string DebugOverviewCameraAutomationHelpText => AutomationProperties.GetHelpText(OverviewCameraButton);

    internal string DebugWorldAutomationName => AutomationProperties.GetName(WorldRoot);

    internal string DebugWorldAutomationHelpText => AutomationProperties.GetHelpText(WorldRoot);

    internal int DebugMiniMapMarkerCount => WorldMiniMapCanvas.Children
        .OfType<Ellipse>()
        .Count();

    internal IReadOnlyList<object> DebugMiniMapFrameElements => WorldMiniMapCanvas.Children
        .OfType<Shape>()
        .Where(shape => shape is not Ellipse)
        .Cast<object>()
        .ToArray();

    internal IReadOnlyList<object> DebugMiniMapMarkerElements => WorldMiniMapCanvas.Children
        .OfType<Ellipse>()
        .Cast<object>()
        .ToArray();

    internal IReadOnlyDictionary<string, Point> DebugMiniMapMarkerCenters => miniMapMarkers.ToDictionary(
        item => item.Key,
        item => new Point(
            Canvas.GetLeft(item.Value) + (item.Value.Width / 2),
            Canvas.GetTop(item.Value) + (item.Value.Height / 2)),
        StringComparer.OrdinalIgnoreCase);

    internal bool DebugMiniMapCameraTargetVisible => miniMapCameraTargetMarker.Visibility == Visibility.Visible;

    internal object DebugMiniMapCameraTargetElement => miniMapCameraTargetMarker;

    internal Point DebugMiniMapCameraTargetCenter => new(
        ReadCanvasValue(Canvas.GetLeft(miniMapCameraTargetMarker)) + (miniMapCameraTargetMarker.Width / 2),
        ReadCanvasValue(Canvas.GetTop(miniMapCameraTargetMarker)) + (miniMapCameraTargetMarker.Height / 2));

    internal string DebugInspectorName => InspectorNameText.Text;

    internal string DebugInspectorModel => InspectorModelText.Text;

    internal string DebugInspectorRole => InspectorRoleText.Text;

    internal string DebugInspectorLastMessage => InspectorLastMessageText.Text;

    internal string DebugInspectorNotes => InspectorNotesText.Text;

    internal int DebugInspectorEventCount => InspectorEventItems.Children.Count;

    internal IReadOnlyList<string> DebugInspectorEventTexts => InspectorEventItems.Children
        .OfType<TextBlock>()
        .Select(text => text.Text)
        .ToArray();

    internal IReadOnlyList<Point> DebugAgentPositions => agentVisuals
        .Select(visual => new Point(visual.Translate.OffsetX, visual.Translate.OffsetZ))
        .ToArray();

    internal IReadOnlyList<string> DebugAgentIds => agentVisuals
        .Select(visual => visual.Avatar.Id)
        .ToArray();

    internal IReadOnlyList<string> DebugAgentNameTagTexts => agentVisuals
        .Select(visual => TextContent(visual.NameTag))
        .ToArray();

    internal IReadOnlyList<string> DebugAgentNameTagAutomationNames => agentVisuals
        .Select(visual => AutomationProperties.GetName(visual.NameTag))
        .ToArray();

    internal IReadOnlyList<string> DebugAgentNameTagAutomationHelpTexts => agentVisuals
        .Select(visual => AutomationProperties.GetHelpText(visual.NameTag))
        .ToArray();

    internal IReadOnlyList<double> DebugAgentFacingAngles => agentVisuals
        .Select(visual => visual.Rotate.Angle)
        .ToArray();

    internal double DebugMinimumAgentSeparation => MinimumAgentSeparation();

    internal int DebugAgentLegPartCount => agentVisuals.Sum(visual => visual.LegPartCount);

    internal int DebugAgentShadowPartCount => agentVisuals.Sum(visual => visual.ShadowPartCount);

    internal int DebugSpeakerSpotlightPartCount => agentVisuals.Sum(visual => visual.SpotlightPartCount);

    internal int DebugLockedBadgePartCount => agentVisuals.Sum(visual => visual.LockedBadgePartCount);

    internal int DebugNarratorIdentityPartCount => agentVisuals.Sum(visual => visual.NarratorIdentityPartCount);

    internal int DebugActivityPropPartCount => agentVisuals.Sum(visual => visual.ActivityPropPartCount);

    internal int DebugVoicePressurePartCount => agentVisuals.Sum(visual => visual.VoicePressurePartCount);

    internal int DebugGeometryModelCount => GeometryModels(sceneGroup).Count();

    internal int DebugWorldSceneryModelCount => DebugGeometryModelCount - agentVisuals.Sum(visual =>
        GeometryModels(visual.Model).Count() + GeometryModels(visual.Shadow).Count());

    internal int DebugUnfrozenGeometryMaterialCount => GeometryModels(sceneGroup)
        .Count(model => !MaterialIsFrozen(model.Material) || !MaterialIsFrozen(model.BackMaterial));

    internal int DebugDistinctGeometryMeshCount => GeometryModels(sceneGroup)
        .Select(model => model.Geometry)
        .Where(geometry => geometry is not null)
        .Distinct(ReferenceEqualityComparer.Instance)
        .Count();

    internal int DebugDistinctGeometryMaterialCount => GeometryModels(sceneGroup)
        .SelectMany(model => new[] { model.Material, model.BackMaterial })
        .Where(material => material is not null)
        .Distinct(ReferenceEqualityComparer.Instance)
        .Count();

    internal GeometryModel3D? DebugFirstAgentGeometryModel => agentVisuals
        .SelectMany(visual => GeometryModels(visual.Shadow).Concat(GeometryModels(visual.Model)))
        .FirstOrDefault();

    internal static int DebugMaterialCacheCapacity => MaterialCacheCapacity;

    internal static int DebugMaterialCacheCount
    {
        get
        {
            lock (MaterialCacheLock)
            {
                return MaterialCache.Count;
            }
        }
    }

    internal static (int Boxes, int Spheres, int Cylinders) DebugMeshCacheCounts
    {
        get
        {
            lock (BoxMeshCacheLock)
            {
                lock (RoundMeshCacheLock)
                {
                    return (BoxMeshCache.Count, SphereMeshCache.Count, CylinderMeshCache.Count);
                }
            }
        }
    }

    internal static bool DebugMaterialCacheContains(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        lock (MaterialCacheLock)
        {
            return MaterialCache.Values.Any(entry => ReferenceEquals(entry.Material, material));
        }
    }

    internal IReadOnlyList<string> DebugAgentAccentColors => agentVisuals
        .Select(visual => visual.Accent.ToString())
        .ToArray();

    internal static IReadOnlyList<Point> DebugResolveCollisionPoints(IReadOnlyList<Point> points)
    {
        var positions = points.ToArray();
        var phases = Enumerable.Range(0, positions.Length)
            .Select(index => index * 1.618033988749895)
            .ToArray();
        ResolveAgentCollisions(positions, phases);
        return positions;
    }

    internal IReadOnlyList<double> DebugSpeakerBubbleAnchorDistances => agentVisuals
        .Where(visual => visual.Avatar.Speaking && visual.Bubble.Visibility == Visibility.Visible)
        .Select(SpeakerBubbleAnchorDistance)
        .ToArray();

    internal IReadOnlyList<string> DebugSpeakerBubbleAutomationNames => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => AutomationProperties.GetName(visual.Bubble))
        .ToArray();

    internal IReadOnlyList<string> DebugSpeakerBubbleAutomationHelpTexts => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => AutomationProperties.GetHelpText(visual.Bubble))
        .ToArray();

    internal IReadOnlyList<string> DebugSpeakerBubbleTexts => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => TextContent(visual.Bubble))
        .ToArray();

    internal IReadOnlyList<string> DebugSpeakerBubbleAutomationStatuses => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => AutomationProperties.GetItemStatus(visual.Bubble))
        .ToArray();

    internal IReadOnlyList<double> DebugSpeakerBubbleBorderThicknesses => agentVisuals
        .Where(visual => visual.Avatar.Speaking && visual.Bubble.Tag is BubbleChrome)
        .Select(visual => ((BubbleChrome)visual.Bubble.Tag).Body.BorderThickness.Left)
        .ToArray();

    internal int DebugAttentionHaloVisibleCount => agentVisuals
        .Count(visual => visual.AttentionHalo.Visibility == Visibility.Visible);

    internal IReadOnlyList<string> DebugVisibleAttentionHaloIds => agentVisuals
        .Where(visual => visual.AttentionHalo.Visibility == Visibility.Visible)
        .Select(visual => visual.Avatar.Id)
        .ToArray();

    internal IReadOnlyList<double> DebugAttentionHaloAnchorDistances => agentVisuals
        .Where(visual => visual.AttentionHalo.Visibility == Visibility.Visible)
        .Select(AttentionHaloAnchorDistance)
        .ToArray();

    internal IReadOnlyList<double> DebugSpeakingJumpOffsets => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => visual.Translate.OffsetY)
        .ToArray();

    internal IReadOnlyList<double> DebugSpeakingArmGestureAngles => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => Math.Max(Math.Abs(visual.Gesture.LeftArmSpread.Angle), Math.Abs(visual.Gesture.RightArmSpread.Angle)))
        .ToArray();

    internal IReadOnlyList<double> DebugSpeakingLegGestureAngles => agentVisuals
        .Where(visual => visual.Avatar.Speaking)
        .Select(visual => MaxAbs(
            visual.Gesture.LeftLegSwing.Angle,
            visual.Gesture.RightLegSwing.Angle,
            visual.Gesture.LeftKneeBend.Angle,
            visual.Gesture.RightKneeBend.Angle,
            visual.Gesture.LeftFootPitch.Angle,
            visual.Gesture.RightFootPitch.Angle))
        .ToArray();

    internal bool DebugAgentNameTagUsesInteractiveOverlayGuard(int index)
    {
        return index >= 0
            && index < agentVisuals.Count
            && IsInteractiveOverlay(agentVisuals[index].NameTag);
    }

    internal bool DebugAgentBubbleUsesInteractiveOverlayGuard(int index)
    {
        return index >= 0
            && index < agentVisuals.Count
            && IsInteractiveOverlay(agentVisuals[index].Bubble);
    }

    internal static BubblePlacement DebugCalculateBubblePlacement(Size bubbleSize, Point anchor, Size canvas)
    {
        return CalculateBubblePlacement(bubbleSize, anchor.X, anchor.Y, canvas.Width, canvas.Height);
    }

    internal static Point DebugClampBubbleAnchor(Point projected, Size canvas)
    {
        return ClampBubbleAnchor(projected, canvas.Width, canvas.Height);
    }

    internal static Rect DebugCalculateMiniMapMarkerPlacement(double x, double z, bool selected)
    {
        return CalculateMiniMapMarkerPlacement(x, z, selected);
    }

    internal bool DebugWheelOverControlPanelLeavesCameraDistance(int delta)
    {
        return DebugWheelLeavesCameraDistance(WorldControlPanel, delta);
    }

    internal bool DebugWheelOverWorldChangesCameraDistance(int delta)
    {
        var before = cameraDistance;
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = MouseWheelEvent
        };
        WorldRoot.RaiseEvent(args);
        return args.Handled && Math.Abs(cameraDistance - before) > 0.001;
    }

    public AgentWorld3DControl()
        : this(() => SystemMotionPreferences.AnimationsEnabled, observeSystemMotionPreferences: true)
    {
    }

    internal AgentWorld3DControl(Func<bool> animationsEnabledProvider)
        : this(animationsEnabledProvider, observeSystemMotionPreferences: false)
    {
    }

    private AgentWorld3DControl(
        Func<bool> animationsEnabledProvider,
        bool observeSystemMotionPreferences)
    {
        this.animationsEnabledProvider = animationsEnabledProvider ?? throw new ArgumentNullException(nameof(animationsEnabledProvider));
        this.observeSystemMotionPreferences = observeSystemMotionPreferences;
        InitializeComponent();
        camera = new PerspectiveCamera
        {
            FieldOfView = 42
        };
        WorldViewport.Camera = camera;
        WorldViewport.Children.Add(new ModelVisual3D { Content = sceneGroup });

        animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        animationTimer.Tick += OnAnimationTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateAnimationState();
        SizeChanged += (_, _) =>
        {
            UpdateHudLayout();
            PositionOverlays();
            PositionMiniMap();
        };
        WorldRoot.MouseLeftButtonDown += OnWorldMouseButtonDown;
        WorldRoot.MouseRightButtonDown += OnWorldMouseButtonDown;
        WorldRoot.MouseMove += OnWorldMouseMove;
        WorldRoot.MouseLeftButtonUp += OnWorldMouseButtonUp;
        WorldRoot.MouseRightButtonUp += OnWorldMouseButtonUp;
        WorldRoot.MouseWheel += OnWorldMouseWheel;
        WorldRoot.MouseLeave += OnWorldMouseLeave;
        WorldRoot.KeyDown += OnWorldKeyDown;
        WorldMiniMapCanvas.MouseLeftButtonUp += OnMiniMapMouseLeftButtonUp;
        WorldRoot.Focusable = true;
        AutomationProperties.SetName(WorldRoot, "AI World 3D arena");
        AutomationProperties.SetHelpText(
            WorldRoot,
            "Keyboard shortcuts: F or Home follows the speaker, R resets the camera, O shows overview, N and P cycle agents, C toggles cinematic camera, arrow keys orbit, Shift plus arrow keys pan, plus and minus zoom.");
        UpdateMotionAutomationState();
        UpdateCameraModeButtons();
        UpdateHudLayout();
        RebuildScene();
        UpdateCamera(immediate: true);
    }

    public void ApplySnapshot(ArenaViewSnapshot snapshot)
    {
        snapshotApplyCount++;
        var firstSnapshot = currentWorld is null;
        var previousSessionId = currentWorld?.SessionId ?? "";
        currentWorld = AgentWorldLayout.Build(snapshot);
        var sessionChanged = !string.IsNullOrWhiteSpace(previousSessionId) &&
            !previousSessionId.Equals(currentWorld.SessionId, StringComparison.OrdinalIgnoreCase);

        // Rebuild-diffing: when nothing render-affecting changed (a common case on refresh
        // ticks and view switches), keep the existing scene and let the animation timer run.
        var signature = WorldSignature(currentWorld);
        if (!firstSnapshot && !sessionChanged && agentVisuals.Count > 0 && signature == lastWorldSignature)
        {
            return;
        }

        lastWorldSignature = signature;
        var shouldSnapCamera = firstSnapshot || sessionChanged || currentWorld.Avatars.Count == 0;
        worldPulseSummary = WorldPulseSummary(currentWorld.Pulse);
        WorldStatusText.Text = WorldStatus(currentWorld);
        WorldStatusText.ToolTip = worldPulseSummary;
        AutomationProperties.SetName(WorldStatusText, "AI World pulse");
        AutomationProperties.SetHelpText(WorldStatusText, WorldStatusText.Text);
        WorldBadge.ToolTip = worldPulseSummary;
        AutomationProperties.SetHelpText(WorldBadge, worldPulseSummary);
        EmptyStatePanel.Visibility = currentWorld.Avatars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorldBadge.Visibility = currentWorld.Avatars.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (currentWorld.Avatars.Count == 0 || sessionChanged)
        {
            ResetWorldViewState();
        }

        RebuildScene();
        EnsureSelection();
        PopulateCuePanel();
        PopulateLegend();
        PopulateInspector();
        UpdateHudLayout();
        UpdateAnimationState();
        UpdateCamera(immediate: shouldSnapCamera);
        PositionOverlays();
        PositionMiniMap();
    }

    private string WorldSignature(AgentWorldSnapshot world)
    {
        // Records' generated ToString covers every render-affecting field, so any content
        // change busts the cache. Theme colours are appended so a recolour also rebuilds.
        var builder = new System.Text.StringBuilder();
        builder.Append(world.TurnIndex).Append('|').Append(world.MessageCount).Append('|');
        builder.Append(world.Pulse).Append('|');
        foreach (var avatar in world.Avatars)
        {
            builder.Append(avatar).Append('\n');
        }

        builder.Append(ResourceColor("PrimaryBorderBrush", Colors.Black)).Append('|');
        builder.Append(ResourceColor("PanelBrush", Colors.Black)).Append('|');
        builder.Append(ResourceColor("InputBrush", Colors.Black));
        return builder.ToString();
    }

    internal int DebugSceneRebuildCount => sceneRebuildCount;

    private void RebuildScene()
    {
        sceneRebuildCount++;
        // Each snapshot rebuilds the scene, so carry every agent's live world position
        // across the rebuild by id - otherwise goal-directed agents teleport back to their
        // spawn ring every turn instead of staying where they walked to.
        var carriedPositions = agentVisuals.ToDictionary(
            visual => visual.Avatar.Id,
            visual => new Point(visual.Translate.OffsetX, visual.Translate.OffsetZ),
            StringComparer.OrdinalIgnoreCase);

        sceneGroup.Children.Clear();
        agentVisuals.Clear();
        OverlayCanvas.Children.Clear();
        miniMapRosterDirty = true;
        miniMapStyleDirty = true;

        foreach (var item in BuildWorldGeometry())
        {
            sceneGroup.Children.Add(item);
        }

        if (currentWorld is null)
        {
            PositionOverlays();
            PositionMiniMap();
            return;
        }

        foreach (var avatar in currentWorld.Avatars)
        {
            var visual = BuildAgentVisual(avatar);
            if (carriedPositions.TryGetValue(avatar.Id, out var carried))
            {
                visual.Translate.OffsetX = carried.X;
                visual.Translate.OffsetZ = carried.Y;
                visual.ShadowTranslate.OffsetX = carried.X;
                visual.ShadowTranslate.OffsetZ = carried.Y;
            }

            sceneGroup.Children.Add(visual.Shadow);
            sceneGroup.Children.Add(visual.Model);
            agentVisuals.Add(visual);
            OverlayCanvas.Children.Add(visual.AttentionHalo);
            OverlayCanvas.Children.Add(visual.NameTag);
            OverlayCanvas.Children.Add(visual.Bubble);
        }

        AnimateAgents();
        PositionOverlays();
        PositionMiniMap();
    }

    private static string WorldStatus(AgentWorldSnapshot world)
    {
        var parts = new List<string>
        {
            $"{world.Pulse.ActiveCount} active agents",
            $"next slot {world.TurnIndex}",
            $"{world.MessageCount} messages"
        };
        if (world.Pulse.LatestTurn > 0)
        {
            parts.Add($"latest turn {world.Pulse.LatestTurn}");
        }

        AddPulseCount(parts, world.Pulse.ThinkingCount, "thinking");
        AddPulseCount(parts, world.Pulse.AlertCount, "alert");
        AddPulseCount(parts, world.Pulse.ToolActivityCount, "tool");
        AddPulseCount(parts, world.Pulse.InternetActivityCount, "web");
        AddPulseCount(parts, world.Pulse.LockedCount, "locked");
        if (world.Pulse.LatestTotalTokens > 0)
        {
            parts.Add($"latest ~{CompactWorldCount(world.Pulse.LatestTotalTokens)} tok");
        }

        if (world.Pulse.SpeakingCount <= 0 || string.IsNullOrWhiteSpace(world.Pulse.SpeakerName))
        {
            parts.Add("scanning arena");
            return string.Join(" | ", parts);
        }

        var watchingCount = Math.Max(0, world.Pulse.ActiveCount - world.Pulse.SpeakingCount);
        var watchText = watchingCount == 1 ? "1 watching" : $"{watchingCount} watching";
        parts.Add($"{world.Pulse.SpeakerName} speaking");
        parts.Add(watchText);
        return string.Join(" | ", parts);
    }

    private static string WorldPulseSummary(AgentWorldPulse pulse)
    {
        var parts = new List<string>();
        if (pulse.LatestTurn > 0)
        {
            parts.Add($"latest turn {pulse.LatestTurn}");
        }

        AddPulseCount(parts, pulse.ThinkingCount, "thinking");
        AddPulseCount(parts, pulse.AlertCount, "alert");
        AddPulseCount(parts, pulse.ToolActivityCount, "tool");
        AddPulseCount(parts, pulse.InternetActivityCount, "web");
        AddPulseCount(parts, pulse.LockedCount, "locked");
        if (pulse.LatestTotalTokens > 0)
        {
            parts.Add($"latest ~{CompactWorldCount(pulse.LatestTotalTokens)} tok");
        }

        if (!string.IsNullOrWhiteSpace(pulse.SpeakerName))
        {
            parts.Add(pulse.SpeakerTurn > 0
                ? $"{pulse.SpeakerName} speaking turn {pulse.SpeakerTurn}"
                : $"{pulse.SpeakerName} speaking");
        }

        return parts.Count == 0 ? "stable arena" : string.Join(" | ", parts);
    }

    private static void AddPulseCount(List<string> parts, int count, string label)
    {
        if (count <= 0)
        {
            return;
        }

        parts.Add($"{count} {label}");
    }

    private static string CompactWorldCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{(value / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}m";
        }

        if (value >= 1_000)
        {
            return $"{(value / 1_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}k";
        }

        return Math.Max(0, value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private IEnumerable<Model3D> BuildWorldGeometry()
    {
        var models = new List<Model3D>
        {
            new AmbientLight(Color.FromRgb(72, 80, 86)),
            new DirectionalLight(Color.FromRgb(242, 252, 246), new Vector3D(-0.42, -0.92, -0.36)),
            new DirectionalLight(Color.FromRgb(96, 206, 180), new Vector3D(0.55, -0.36, 0.78)),
            speakerAccentLight,
            CreateBox(new Point3D(0, -0.055, 0), 18.5, 0.08, 13.2, ResourceColor("InputBrush", Color.FromRgb(12, 20, 18))),
            CreateBox(new Point3D(0, 0.005, 0), 16.7, 0.025, 11.55, Blend(ResourceColor("PanelBrush", Color.FromRgb(24, 35, 31)), ResourceColor("PrimaryBorderBrush", Color.FromRgb(46, 168, 137)), 0.07)),
            CreateBox(new Point3D(0, 0.045, 0), 3.2, 0.08, 1.7, Blend(ResourceColor("PrimaryBrush", Color.FromRgb(16, 84, 68)), Colors.Black, 0.35)),
            CreateBox(new Point3D(0, 0.105, 0), 2.28, 0.06, 1.1, Blend(ResourceColor("PrimaryBorderBrush", Color.FromRgb(46, 168, 137)), Colors.Black, 0.28))
        };

        var gridColor = Blend(ResourceColor("ControlBorderBrush", Color.FromRgb(72, 100, 90)), Colors.Black, 0.35);
        for (var x = -8; x <= 8; x++)
        {
            models.Add(CreateBox(new Point3D(x, 0.01, 0), 0.018, 0.026, 10.7, gridColor, 0.55));
        }

        for (var z = -5; z <= 5; z++)
        {
            models.Add(CreateBox(new Point3D(0, 0.012, z), 16.0, 0.026, 0.018, gridColor, 0.55));
        }

        var accent = ResourceColor("PrimaryBorderBrush", Color.FromRgb(46, 168, 137));
        var assist = ResourceColor("AssistBorderBrush", Color.FromRgb(225, 125, 182));
        var beta = ResourceColor("BetaAccentBrush", Color.FromRgb(240, 195, 106));
        models.Add(CreateBox(new Point3D(-8.7, 0.08, 0), 0.08, 0.2, 12.2, accent, 0.75));
        models.Add(CreateBox(new Point3D(8.7, 0.08, 0), 0.08, 0.2, 12.2, accent, 0.75));
        models.Add(CreateBox(new Point3D(0, 0.08, -6.1), 17.5, 0.2, 0.08, accent, 0.75));
        models.Add(CreateBox(new Point3D(0, 0.08, 6.1), 17.5, 0.2, 0.08, accent, 0.75));
        models.Add(CreateBox(new Point3D(0, 0.075, -3.05), 15.4, 0.035, 0.18, Blend(accent, Colors.Black, 0.24), 0.56));
        models.Add(CreateBox(new Point3D(0, 0.075, 3.05), 15.4, 0.035, 0.18, Blend(accent, Colors.Black, 0.24), 0.56));
        models.Add(CreateBox(new Point3D(-4.4, 0.076, 0), 0.18, 0.035, 10.4, Blend(accent, Colors.Black, 0.24), 0.48));
        models.Add(CreateBox(new Point3D(4.4, 0.076, 0), 0.18, 0.035, 10.4, Blend(accent, Colors.Black, 0.24), 0.48));
        models.AddRange(CreateArenaConsoleGeometry(accent, assist, beta));

        // Token skyline: the back row of towers reads each active agent's latest token load,
        // so the arena grows a live bar chart as the debate runs. Heights are quantized so the
        // shared box-mesh cache keeps reusing geometry across turns.
        var towerBase = Blend(ResourceColor("CardBrush", Color.FromRgb(20, 32, 27)), Colors.White, 0.05);
        var fallbackHeights = new[] { 0.6, 0.78, 0.52, 0.7, 0.58, 0.84, 0.5, 0.74, 0.56 };
        var towerAvatars = currentWorld?.Avatars ?? [];
        var maxTokens = Math.Max(1, towerAvatars.Select(item => item.TotalTokens).DefaultIfEmpty(0).Max());
        for (var index = 0; index < fallbackHeights.Length; index++)
        {
            var x = -7.2 + (index * 1.8);
            double h;
            Color cap;
            if (towerAvatars.Count > 0)
            {
                var avatar = towerAvatars[index % towerAvatars.Count];
                var normalized = Math.Clamp(avatar.TotalTokens / (double)maxTokens, 0, 1);
                h = Math.Round((0.45 + (normalized * 1.6)) / 0.05) * 0.05;
                cap = Blend(AccentColor(avatar), Colors.White, 0.18);
            }
            else
            {
                h = fallbackHeights[index];
                cap = Blend(accent, Colors.White, 0.18);
            }

            models.Add(CreateBox(new Point3D(x, h / 2, -6.75), 0.42, h, 0.34, towerBase));
            models.Add(CreateBox(new Point3D(x, h + 0.055, -6.75), 0.52, 0.08, 0.42, cap, 0.85));
        }

        // Data pylons - in-arena obstacles the agents must navigate around (see WorldObstacles).
        var pylonAccents = new[] { accent, assist, beta, ResourceColor("DeltaAccentBrush", Color.FromRgb(158, 166, 255)) };
        var pylonSpots = WorldObstacles.Skip(1).ToArray();
        for (var index = 0; index < pylonSpots.Length; index++)
        {
            var (px, pz, _) = pylonSpots[index];
            var glow = pylonAccents[index % pylonAccents.Length];
            models.Add(CreateCylinder(new Point3D(px, 0.5, pz), 0.34, 1.0, Blend(towerBase, glow, 0.18)));
            models.Add(CreateCylinder(new Point3D(px, 1.02, pz), 0.2, 0.16, Blend(glow, Colors.White, 0.22), 0.92));
            models.Add(CreateSphere(new Point3D(px, 1.2, pz), 0.12, Blend(glow, Colors.White, 0.32), 0.9));
        }

        return models;
    }

    private IEnumerable<Model3D> CreateArenaConsoleGeometry(Color accent, Color assist, Color beta)
    {
        var glass = Blend(accent, Colors.White, 0.34);
        var darkGlass = Blend(ResourceColor("InputBrush", Color.FromRgb(13, 23, 20)), accent, 0.2);
        var rail = Blend(accent, Colors.Black, 0.22);
        var trim = Blend(accent, Colors.White, 0.24);

        yield return CreateBox(new Point3D(0, 0.34, 0), 1.18, 0.38, 0.62, darkGlass, 0.34);
        yield return CreateBox(new Point3D(0, 0.62, 0), 0.92, 0.08, 0.46, glass, 0.62);
        yield return CreateBox(new Point3D(0, 0.78, 0), 0.62, 0.2, 0.035, trim, 0.56);
        yield return CreateBox(new Point3D(0, 0.78, 0), 0.035, 0.2, 0.62, trim, 0.56);

        foreach (var (x, z, color, height) in new[]
                 {
                     (-7.55, -5.18, accent, 0.76),
                     (7.55, -5.18, assist, 0.68),
                     (-7.55, 5.18, beta, 0.72),
                     (7.55, 5.18, accent, 0.82)
                 })
        {
            yield return CreateBox(new Point3D(x, height / 2, z), 0.28, height, 0.28, Blend(color, Colors.Black, 0.2), 0.58);
            yield return CreateBox(new Point3D(x, height + 0.08, z), 0.52, 0.08, 0.52, Blend(color, Colors.White, 0.24), 0.72);
        }

        foreach (var z in new[] { -1.42, 1.42 })
        {
            yield return CreateBox(new Point3D(0, 0.16, z), 3.92, 0.08, 0.1, rail, 0.46);
            yield return CreateBox(new Point3D(-2.02, 0.22, z), 0.1, 0.2, 0.1, rail, 0.54);
            yield return CreateBox(new Point3D(2.02, 0.22, z), 0.1, 0.2, 0.1, rail, 0.54);
        }

        foreach (var x in new[] { -2.7, 2.7 })
        {
            yield return CreateBox(new Point3D(x, 0.14, 0), 0.08, 0.06, 2.36, Blend(assist, Colors.Black, 0.28), 0.38);
        }
    }

    private WorldAgentVisual BuildAgentVisual(AgentWorldAvatar avatar)
    {
        var accent = AccentColor(avatar);
        var shell = Blend(ResourceColor("PanelBrush", Color.FromRgb(24, 35, 31)), accent, 0.16);
        var trim = Blend(accent, Colors.White, 0.22);
        var dark = Blend(ResourceColor("InputBrush", Color.FromRgb(13, 23, 20)), Colors.Black, 0.22);
        var model = new Model3DGroup();
        var shadow = new Model3DGroup();
        var gesture = new AgentGestureRig(
            new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0),
            new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0),
            new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0));

        var legPartCount = 0;
        var shadowPartCount = 0;
        var spotlightPartCount = 0;
        var lockedBadgePartCount = 0;
        var narratorIdentityPartCount = 0;
        var activityPropPartCount = 0;
        var voicePressurePartCount = 0;
        shadow.Children.Add(CreateBox(new Point3D(0, 0.018, 0.02), 0.72, 0.018, 0.52, Blend(Colors.Black, accent, 0.16), 0.34));
        shadowPartCount++;
        if (avatar.Speaking)
        {
            spotlightPartCount += AddSpeakerSpotlight(shadow, accent);
        }

        var core = Blend(accent, Colors.White, 0.5);
        model.Children.Add(CreateBox(new Point3D(0, 0.06, 0), 0.62, 0.05, 0.46, Blend(accent, Colors.Black, 0.7), 0.48));
        // Sleek sci-fi shell: rounded chassis cap, tapered torso, glowing accent core.
        model.Children.Add(CreateBox(new Point3D(0, 0.36, 0), 0.42, 0.55, 0.3, shell));
        model.Children.Add(CreateCylinder(new Point3D(0, 0.62, 0), 0.215, 0.12, Blend(shell, Colors.White, 0.12)));
        model.Children.Add(CreateSphere(new Point3D(0, 0.45, 0.16), 0.075, core, avatar.Speaking ? 1 : 0.82));
        model.Children.Add(CreateSphere(new Point3D(-0.21, 0.57, 0.02), 0.11, Blend(shell, Colors.White, 0.06)));
        model.Children.Add(CreateSphere(new Point3D(0.21, 0.57, 0.02), 0.11, Blend(shell, Colors.White, 0.06)));
        var headGroup = new Model3DGroup();
        headGroup.Children.Add(CreateSphere(new Point3D(0, 0.78, 0.02), 0.235, Blend(shell, Colors.White, 0.1)));
        headGroup.Children.Add(CreateBox(new Point3D(0, 0.79, 0.205), 0.34, 0.12, 0.06, dark));
        headGroup.Children.Add(CreateBox(new Point3D(0, 0.79, 0.232), 0.28, 0.07, 0.02, Blend(core, Colors.White, 0.2), avatar.Speaking ? 1 : 0.8));
        headGroup.Children.Add(CreateSphere(new Point3D(-0.08, 0.8, 0.236), 0.028, trim));
        headGroup.Children.Add(CreateSphere(new Point3D(0.08, 0.8, 0.236), 0.028, trim));
        var headTransform = new Transform3DGroup();
        headTransform.Children.Add(new RotateTransform3D(gesture.HeadNod, new Point3D(0, 0.72, 0.02)));
        headGroup.Transform = headTransform;
        model.Children.Add(headGroup);

        var leftShoulder = new Point3D(-0.28, 0.58, 0.02);
        var rightShoulder = new Point3D(0.28, 0.58, 0.02);
        model.Children.Add(CreateJointBox(new Point3D(-0.31, 0.39, 0), 0.12, 0.4, 0.16, Blend(shell, Colors.Black, 0.08), gesture.LeftArmLift, gesture.LeftArmSpread, leftShoulder));
        model.Children.Add(CreateJointBox(new Point3D(0.31, 0.39, 0), 0.12, 0.4, 0.16, Blend(shell, Colors.Black, 0.08), gesture.RightArmLift, gesture.RightArmSpread, rightShoulder));
        model.Children.Add(CreateJointBox(new Point3D(-0.31, 0.16, 0.04), 0.15, 0.12, 0.18, trim, gesture.LeftArmLift, gesture.LeftArmSpread, leftShoulder, 0.92));
        model.Children.Add(CreateJointBox(new Point3D(0.31, 0.16, 0.04), 0.15, 0.12, 0.18, trim, gesture.RightArmLift, gesture.RightArmSpread, rightShoulder, 0.92));
        legPartCount += AddRobotLeg(model, x: -0.14, shell, trim, dark, gesture.LeftLegSwing, gesture.LeftKneeBend, gesture.LeftFootPitch);
        legPartCount += AddRobotLeg(model, x: 0.14, shell, trim, dark, gesture.RightLegSwing, gesture.RightKneeBend, gesture.RightFootPitch);
        model.Children.Add(CreateCylinder(new Point3D(0, 0.99, 0.02), 0.022, 0.2, trim, avatar.Speaking ? 1 : 0.65));
        model.Children.Add(CreateSphere(new Point3D(0, 1.12, 0.02), 0.06, Blend(core, Colors.White, 0.2), avatar.Speaking ? 1 : 0.55));
        if (avatar.Thinking)
        {
            model.Children.Add(CreateBox(new Point3D(0, 0.43, -0.04), 0.86, 0.035, 0.62, Blend(trim, Colors.White, 0.2), 0.28));
        }

        voicePressurePartCount += AddVoicePressureCues(model, avatar, accent, trim);
        if (avatar.Locked)
        {
            lockedBadgePartCount += AddLockedBadge(model, accent);
        }

        if (IsNarrator(avatar))
        {
            narratorIdentityPartCount += AddNarratorBooth(model, accent, trim);
        }

        if (avatar.HasError)
        {
            model.Children.Add(CreateBox(new Point3D(-0.3, 1.18, 0.02), 0.12, 0.12, 0.12, ResourceColor("DangerTextBrush", Color.FromRgb(255, 123, 130)), 0.96));
            activityPropPartCount += AddErrorBeacon(model);
        }

        if (avatar.HasToolActivity)
        {
            model.Children.Add(CreateBox(new Point3D(0.3, 1.18, 0.02), 0.16, 0.08, 0.16, ResourceColor("AssistBorderBrush", Color.FromRgb(225, 125, 182)), 0.9));
            activityPropPartCount += AddToolSatellite(model);
        }

        if (avatar.HasInternetActivity)
        {
            model.Children.Add(CreateBox(new Point3D(0, 1.23, -0.22), 0.18, 0.06, 0.18, ResourceColor("BetaAccentBrush", Color.FromRgb(240, 195, 106)), 0.9));
            activityPropPartCount += AddInternetPanel(model);
        }

        if (avatar.Speaking)
        {
            model.Children.Add(CreateBox(new Point3D(0, 1.31, 0.02), 0.34, 0.035, 0.34, Blend(trim, Colors.White, 0.26), 0.76));
            model.Children.Add(CreateBox(new Point3D(0, 0.42, -0.03), 0.78, 0.03, 0.56, Blend(accent, Colors.Black, 0.58), 0.34));
        }

        var transform = new Transform3DGroup();
        var scale = new ScaleTransform3D(1, 1, 1);
        var rotate = new AxisAngleRotation3D(new Vector3D(0, 1, 0), ToDegrees(avatar.FacingRadians));
        var translate = new TranslateTransform3D(avatar.X, 0, avatar.Z);
        transform.Children.Add(scale);
        transform.Children.Add(new RotateTransform3D(gesture.BodyLean, new Point3D(0, 0.08, 0)));
        transform.Children.Add(new RotateTransform3D(rotate));
        transform.Children.Add(translate);
        model.Transform = transform;

        var shadowTransform = new Transform3DGroup();
        var shadowScale = new ScaleTransform3D(1, 1, 1);
        var shadowTranslate = new TranslateTransform3D(avatar.X, 0, avatar.Z);
        shadowTransform.Children.Add(shadowScale);
        shadowTransform.Children.Add(shadowTranslate);
        shadow.Transform = shadowTransform;

        var attentionScale = new ScaleTransform(1, 1);
        var attentionHalo = CreateAttentionHalo(accent, attentionScale);
        var nameTag = CreateNameTag(avatar, accent);
        var bubble = CreateBubble(avatar, accent);
        nameTag.Tag = avatar.Id;
        nameTag.Cursor = Cursors.Hand;
        nameTag.Focusable = true;
        nameTag.IsHitTestVisible = true;
        nameTag.MouseLeftButtonUp += AgentOverlay_MouseLeftButtonUp;
        nameTag.KeyDown += AgentOverlay_KeyDown;
        bubble.DataContext = avatar.Id;
        bubble.Cursor = Cursors.Hand;
        bubble.Focusable = avatar.Speaking;
        bubble.IsHitTestVisible = true;
        bubble.MouseLeftButtonUp += AgentOverlay_MouseLeftButtonUp;
        bubble.KeyDown += AgentOverlay_KeyDown;
        return new WorldAgentVisual(
            avatar,
            accent,
            $"FOLLOWING {avatar.Name.ToUpperInvariant()}",
            model,
            shadow,
            scale,
            rotate,
            translate,
            shadowScale,
            shadowTranslate,
            gesture,
            attentionHalo,
            attentionScale,
            nameTag,
            bubble,
            legPartCount,
            shadowPartCount,
            spotlightPartCount,
            lockedBadgePartCount,
            narratorIdentityPartCount,
            activityPropPartCount,
            voicePressurePartCount);
    }

    private int AddVoicePressureCues(Model3DGroup model, AgentWorldAvatar avatar, Color accent, Color trim)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(avatar.VoiceStyle) &&
            !avatar.VoiceStyle.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            model.Children.Add(CreateBox(new Point3D(0, 0.68, 0.255), 0.42, 0.035, 0.035, Blend(trim, Colors.White, 0.22), 0.82));
            count++;
        }

        if (!string.IsNullOrWhiteSpace(avatar.PressureProfile) &&
            !avatar.PressureProfile.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            model.Children.Add(CreateBox(new Point3D(0, 0.29, 0.185), 0.48, 0.035, 0.035, Blend(accent, Colors.Black, 0.16), 0.76));
            model.Children.Add(CreateBox(new Point3D(0, 0.235, 0.185), 0.36, 0.03, 0.03, Blend(accent, Colors.White, 0.18), 0.68));
            count += 2;
        }

        return count;
    }

    private int AddLockedBadge(Model3DGroup model, Color accent)
    {
        var lockColor = Blend(accent, Colors.White, 0.32);
        model.Children.Add(CreateBox(new Point3D(-0.34, 0.97, 0.13), 0.18, 0.16, 0.04, lockColor, 0.88));
        model.Children.Add(CreateBox(new Point3D(-0.34, 1.08, 0.13), 0.13, 0.035, 0.04, lockColor, 0.8));
        model.Children.Add(CreateBox(new Point3D(-0.4, 1.035, 0.13), 0.035, 0.09, 0.035, lockColor, 0.72));
        model.Children.Add(CreateBox(new Point3D(-0.28, 1.035, 0.13), 0.035, 0.09, 0.035, lockColor, 0.72));
        return 4;
    }

    private int AddNarratorBooth(Model3DGroup model, Color accent, Color trim)
    {
        var booth = Blend(accent, Colors.Black, 0.34);
        model.Children.Add(CreateBox(new Point3D(0, 0.32, -0.24), 0.8, 0.44, 0.09, booth, 0.48));
        model.Children.Add(CreateBox(new Point3D(0, 0.6, -0.24), 0.62, 0.08, 0.12, Blend(trim, Colors.White, 0.18), 0.72));
        model.Children.Add(CreateBox(new Point3D(-0.36, 0.82, 0.02), 0.08, 0.36, 0.08, Blend(accent, Colors.White, 0.2), 0.66));
        model.Children.Add(CreateBox(new Point3D(0.36, 0.82, 0.02), 0.08, 0.36, 0.08, Blend(accent, Colors.White, 0.2), 0.66));
        model.Children.Add(CreateBox(new Point3D(0, 1.34, 0.02), 0.42, 0.05, 0.42, Blend(trim, Colors.White, 0.3), 0.78));
        return 5;
    }

    private int AddErrorBeacon(Model3DGroup model)
    {
        var danger = ResourceColor("DangerTextBrush", Color.FromRgb(255, 123, 130));
        model.Children.Add(CreateBox(new Point3D(-0.42, 1.04, 0.18), 0.06, 0.34, 0.06, danger, 0.78));
        model.Children.Add(CreateBox(new Point3D(-0.42, 1.25, 0.18), 0.22, 0.08, 0.08, danger, 0.84));
        return 2;
    }

    private int AddToolSatellite(Model3DGroup model)
    {
        var assist = ResourceColor("AssistBorderBrush", Color.FromRgb(225, 125, 182));
        model.Children.Add(CreateBox(new Point3D(0.46, 0.98, 0.12), 0.08, 0.34, 0.08, Blend(assist, Colors.White, 0.16), 0.66));
        model.Children.Add(CreateBox(new Point3D(0.58, 1.18, 0.12), 0.2, 0.08, 0.16, assist, 0.84));
        model.Children.Add(CreateBox(new Point3D(0.7, 1.18, 0.12), 0.06, 0.22, 0.06, assist, 0.72));
        return 3;
    }

    private int AddInternetPanel(Model3DGroup model)
    {
        var webAccent = ResourceColor("BetaAccentBrush", Color.FromRgb(240, 195, 106));
        model.Children.Add(CreateBox(new Point3D(0, 1.08, -0.36), 0.42, 0.24, 0.04, Blend(webAccent, Colors.White, 0.1), 0.82));
        model.Children.Add(CreateBox(new Point3D(0, 1.25, -0.36), 0.32, 0.035, 0.05, webAccent, 0.72));
        model.Children.Add(CreateBox(new Point3D(0, 0.91, -0.36), 0.32, 0.035, 0.05, webAccent, 0.72));
        return 3;
    }

    private int AddSpeakerSpotlight(Model3DGroup shadow, Color accent)
    {
        var outer = Blend(accent, Colors.White, 0.18);
        var inner = Blend(accent, Colors.Black, 0.28);
        shadow.Children.Add(CreateBox(new Point3D(0, 0.035, -0.42), 0.92, 0.02, 0.035, outer, 0.72));
        shadow.Children.Add(CreateBox(new Point3D(0, 0.035, 0.42), 0.92, 0.02, 0.035, outer, 0.72));
        shadow.Children.Add(CreateBox(new Point3D(-0.46, 0.035, 0), 0.035, 0.02, 0.78, inner, 0.6));
        shadow.Children.Add(CreateBox(new Point3D(0.46, 0.035, 0), 0.035, 0.02, 0.78, inner, 0.6));
        shadow.Children.Add(CreateBox(new Point3D(0, 0.045, -0.62), 0.22, 0.018, 0.035, outer, 0.64));
        shadow.Children.Add(CreateBox(new Point3D(0, 0.045, 0.62), 0.22, 0.018, 0.035, outer, 0.64));
        shadow.Children.Add(CreateBox(new Point3D(-0.68, 0.045, 0), 0.035, 0.018, 0.22, outer, 0.64));
        shadow.Children.Add(CreateBox(new Point3D(0.68, 0.045, 0), 0.035, 0.018, 0.22, outer, 0.64));
        return 8;
    }

    private int AddRobotLeg(
        Model3DGroup model,
        double x,
        Color shell,
        Color trim,
        Color dark,
        AxisAngleRotation3D legSwing,
        AxisAngleRotation3D kneeBend,
        AxisAngleRotation3D footPitch)
    {
        var hip = new Point3D(x, 0.24, 0);
        var knee = new Point3D(x, 0.12, 0.015);
        var ankle = new Point3D(x, 0.075, 0.04);
        model.Children.Add(CreateBox(new Point3D(x, 0.23, 0), 0.16, 0.08, 0.16, Blend(shell, Colors.Black, 0.22)));
        model.Children.Add(CreateJointBox(new Point3D(x, 0.15, 0.005), 0.105, 0.22, 0.105, Blend(shell, Colors.Black, 0.04), legSwing, kneeBend, hip));
        model.Children.Add(CreateJointBox(new Point3D(x, 0.085, 0.02), 0.095, 0.13, 0.095, Blend(trim, Colors.Black, 0.08), kneeBend, legSwing, knee, 0.96));
        model.Children.Add(CreateJointBox(new Point3D(x, 0.055, 0.075), 0.18, 0.06, 0.28, Blend(dark, trim, 0.22), footPitch, legSwing, ankle));
        return 4;
    }

    private static Border CreateNameTag(AgentWorldAvatar avatar, Color accent)
    {
        var name = new TextBlock
        {
            Text = avatar.Name,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 128
        };
        var state = new TextBlock
        {
            Text = NameTagStatus(avatar),
            Foreground = BrushFrom(Blend(accent, Colors.White, 0.36)),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 128,
            Margin = new Thickness(0, 1, 0, 0)
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        stack.Children.Add(name);
        stack.Children.Add(state);
        var tag = new Border
        {
            Child = stack,
            Background = BrushFrom(Color.FromArgb(210, 12, 19, 17)),
            BorderBrush = BrushFrom(Color.FromArgb(210, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 4, 7, 5),
            CornerRadius = new CornerRadius(6)
        };
        AutomationProperties.SetName(tag, $"{avatar.Name}, {NameTagStatus(avatar)}");
        AutomationProperties.SetHelpText(tag, $"Select and focus this agent. {LegendDetail(avatar)}");
        AutomationProperties.SetItemStatus(tag, LegendDetail(avatar));
        return tag;
    }

    private static Grid CreateAttentionHalo(Color accent, ScaleTransform scale)
    {
        var outerStroke = BrushFrom(Color.FromArgb(210, accent.R, accent.G, accent.B));
        var innerStroke = BrushFrom(Color.FromArgb(150, 255, 255, 255));
        var fill = BrushFrom(Color.FromArgb(28, accent.R, accent.G, accent.B));
        var halo = new Grid
        {
            Width = 76,
            Height = 42,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale
        };

        halo.Children.Add(new Ellipse
        {
            Width = 68,
            Height = 28,
            Fill = fill,
            Stroke = outerStroke,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        halo.Children.Add(new Ellipse
        {
            Width = 38,
            Height = 14,
            Stroke = innerStroke,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 7)
        });
        halo.Children.Add(new Line
        {
            X1 = 13,
            Y1 = 28,
            X2 = 24,
            Y2 = 20,
            Stroke = outerStroke,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        halo.Children.Add(new Line
        {
            X1 = 63,
            Y1 = 28,
            X2 = 52,
            Y2 = 20,
            Stroke = outerStroke,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        return halo;
    }

    private static Border CreateBubble(AgentWorldAvatar avatar, Color accent)
    {
        var bodyBrush = BrushFrom(Color.FromArgb(232, 14, 22, 20));
        var selectedBodyBrush = BrushFrom(Color.FromArgb(246, 14, 22, 20));
        var borderBrush = BrushFrom(Color.FromArgb(235, accent.R, accent.G, accent.B));
        var selectedBorderBrush = BrushFrom(Color.FromArgb(255, 255, 255, 255));
        var header = new TextBlock
        {
            Text = avatar.BubbleTurn > 0 ? $"{avatar.Name} | turn {avatar.BubbleTurn}" : avatar.Name,
            Foreground = BrushFrom(Color.FromArgb(236, accent.R, accent.G, accent.B)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 250,
            Margin = new Thickness(0, 0, 0, 3)
        };
        var text = new TextBlock
        {
            Text = avatar.BubbleText,
            Foreground = Brushes.White,
            FontSize = 12,
            LineHeight = 15,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 250
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(text);
        var body = new Border
        {
            Child = stack,
            Background = bodyBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 6, 9, 7),
            CornerRadius = new CornerRadius(7)
        };
        var pointerTransform = new TranslateTransform();
        var pointer = new Polygon
        {
            Width = BubbleTailWidth,
            Height = BubbleTailHeight,
            Fill = bodyBrush,
            Stroke = borderBrush,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = pointerTransform,
            IsHitTestVisible = false
        };
        var grid = new Grid
        {
            MaxWidth = 270
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 0);
        Grid.SetRow(pointer, 1);
        grid.Children.Add(body);
        grid.Children.Add(pointer);

        var bubble = new Border
        {
            Child = grid,
            Visibility = avatar.Speaking ? Visibility.Visible : Visibility.Collapsed,
            Background = Brushes.Transparent,
            MaxWidth = 270,
            Effect = null,
            ToolTip = avatar.BubbleText,
            Tag = new BubbleChrome(body, pointer, pointerTransform, bodyBrush, selectedBodyBrush, borderBrush, selectedBorderBrush)
        };
        AutomationProperties.SetName(bubble, BubbleAutomationName(avatar));
        AutomationProperties.SetHelpText(bubble, string.IsNullOrWhiteSpace(avatar.BubbleText)
            ? $"Speech bubble for {avatar.Name}."
            : avatar.BubbleText);
        AutomationProperties.SetItemStatus(bubble, avatar.Speaking ? "speaking" : "hidden");
        return bubble;
    }

    private void PopulateLegend()
    {
        WorldLegendItems.Children.Clear();
        if (currentWorld is null || currentWorld.Avatars.Count == 0)
        {
            WorldLegendPanel.Visibility = Visibility.Collapsed;
            return;
        }

        WorldLegendPanel.Visibility = Visibility.Visible;
        foreach (var avatar in currentWorld.Avatars)
        {
            WorldLegendItems.Children.Add(CreateLegendChip(avatar, AccentColor(avatar)));
        }
    }

    private void PopulateCuePanel()
    {
        WorldCueItems.Children.Clear();
        if (currentWorld is null || currentWorld.Avatars.Count == 0 || currentWorld.Cues.Count == 0)
        {
            WorldCuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        WorldCuePanel.Visibility = Visibility.Visible;
        foreach (var cue in currentWorld.Cues)
        {
            WorldCueItems.Children.Add(CreateWorldCueChip(cue));
        }
    }

    private Border CreateWorldCueChip(AgentWorldCue cue)
    {
        var accent = CueAccent(cue.Severity);
        var label = new TextBlock
        {
            Text = cue.Label.ToUpperInvariant(),
            Foreground = BrushFrom(Blend(accent, Colors.White, 0.32)),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 74
        };
        var detail = new TextBlock
        {
            Text = cue.Detail,
            Foreground = ResourceBrush("TextBrush", Colors.White),
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 158
        };
        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(detail);
        var chip = new Border
        {
            Child = stack,
            Background = BrushFrom(Color.FromArgb(205, 12, 19, 17)),
            BorderBrush = BrushFrom(Color.FromArgb(215, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 5),
            Margin = new Thickness(0, 0, 6, 4),
            CornerRadius = new CornerRadius(7),
            ToolTip = $"{cue.Label}: {cue.Detail}"
        };
        AutomationProperties.SetName(chip, $"World cue {cue.Label}: {cue.Detail}");
        AutomationProperties.SetHelpText(chip, $"AI World live cue. Severity {cue.Severity}. {cue.Detail}");
        AutomationProperties.SetItemStatus(chip, cue.Severity);
        return chip;
    }

    private Color CueAccent(string severity)
    {
        return severity.Equals("alert", StringComparison.OrdinalIgnoreCase)
            ? ResourceColor("DangerBorderBrush", Color.FromRgb(238, 94, 112))
            : severity.Equals("active", StringComparison.OrdinalIgnoreCase)
                ? ResourceColor("PrimaryBorderBrush", Color.FromRgb(46, 168, 137))
                : severity.Equals("signal", StringComparison.OrdinalIgnoreCase)
                    ? ResourceColor("AssistBorderBrush", Color.FromRgb(225, 125, 182))
                    : ResourceColor("MutedTextBrush", Color.FromRgb(184, 199, 191));
    }

    private Border CreateLegendChip(AgentWorldAvatar avatar, Color accent)
    {
        var label = new TextBlock
        {
            Text = avatar.Name,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 94
        };
        var detail = new TextBlock
        {
            Text = LegendDetail(avatar),
            Foreground = avatar.Speaking ? BrushFrom(Blend(accent, Colors.White, 0.28)) : ResourceBrush("MutedTextBrush", Color.FromRgb(184, 199, 191)),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 176
        };
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = BrushFrom(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        var text = new StackPanel();
        text.Children.Add(label);
        text.Children.Add(detail);
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        content.Children.Add(dot);
        content.Children.Add(text);
        foreach (var eventChip in EventLabels(avatar).Take(2))
        {
            content.Children.Add(new TextBlock
            {
                Text = eventChip,
                Foreground = BrushFrom(Blend(accent, Colors.White, 0.35)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var chip = new Border
        {
            Child = content,
            Background = BrushFrom(Color.FromArgb(avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase) ? (byte)238 : avatar.Speaking ? (byte)232 : (byte)184, 12, 19, 17)),
            BorderBrush = BrushFrom(Color.FromArgb(avatar.Speaking ? (byte)245 : (byte)150, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 9, 6),
            Margin = new Thickness(0, 0, 8, 4),
            CornerRadius = new CornerRadius(7),
            Cursor = Cursors.Hand,
            Focusable = true,
            Tag = avatar.Id
        };
        chip.MouseLeftButtonUp += AgentOverlay_MouseLeftButtonUp;
        chip.KeyDown += AgentOverlay_KeyDown;
        AutomationProperties.SetName(chip, $"{avatar.Name}, {NameTagStatus(avatar)}");
        AutomationProperties.SetHelpText(chip, $"Select and focus this agent. {LegendDetail(avatar)}");
        AutomationProperties.SetItemStatus(chip, LegendDetail(avatar));
        return chip;
    }

    private void EnsureSelection()
    {
        if (currentWorld is null || currentWorld.Avatars.Count == 0)
        {
            selectedAgentId = "";
            manualSelectionPinned = false;
            return;
        }

        if (manualSelectionPinned &&
            currentWorld.Avatars.Any(avatar => avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        manualSelectionPinned = false;
        selectedAgentId = CurrentSpeakerAvatar()?.Id ?? currentWorld.Avatars[0].Id;
    }

    private void ResetWorldViewState()
    {
        selectedAgentId = "";
        manualSelectionPinned = false;
        inspectorDismissed = false;
        cameraMode = AgentWorldCameraMode.FollowSpeaker;
        cameraTarget = new Point3D(0, 0.72, 0);
        manualPan = new Vector3D(0, 0, 0);
        cameraYaw = 0;
        cameraPitch = 0.56;
        cameraDistance = 12.2;
        preOverviewCameraPitch = 0.56;
        preOverviewCameraDistance = 12.2;
        preOverviewManualPan = new Vector3D(0, 0, 0);
        hasPreOverviewCamera = false;
        dragMode = CameraDragMode.None;
        lastMousePoint = null;
        dragStartPoint = null;
        dragMoved = false;
        if (WorldRoot.IsMouseCaptured)
        {
            WorldRoot.ReleaseMouseCapture();
        }

        UpdateCameraModeButtons();
    }

    private void SelectAgent(string id)
    {
        if (currentWorld is null ||
            !currentWorld.Avatars.Any(avatar => avatar.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        selectedAgentId = id;
        manualSelectionPinned = true;
        inspectorDismissed = false;
        PopulateLegend();
        PopulateInspector();
        PositionMiniMap();
        if (cameraMode == AgentWorldCameraMode.Free)
        {
            SetCameraMode(AgentWorldCameraMode.FollowSpeaker);
        }

        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
    }

    internal void DebugSelectAgent(string id)
    {
        SelectAgent(id);
    }

    internal void DebugClickProjectedAgent(int index)
    {
        if (index < 0 || index >= agentVisuals.Count)
        {
            return;
        }

        var visual = agentVisuals[index];
        var point = ProjectToOverlay(
            AgentWorldPoint(visual, 0.65),
            Math.Max(1, OverlayCanvas.ActualWidth),
            Math.Max(1, OverlayCanvas.ActualHeight),
            rejectOutsideViewport: false);
        if (point is null)
        {
            return;
        }

        SelectNearestAgent(point.Value);
    }

    internal void DebugClickMiniMap(double x, double y)
    {
        HandleMiniMapClick(new Point(x, y));
    }

    internal bool DebugActivateNameTag(string id, Key key)
    {
        var nameTag = agentVisuals
            .FirstOrDefault(visual => visual.Avatar.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?.NameTag;
        return nameTag is not null && TryActivateAgentOverlay(nameTag, key);
    }

    internal bool DebugActivateBubble(string id, Key key)
    {
        var bubble = agentVisuals
            .FirstOrDefault(visual => visual.Avatar.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?.Bubble;
        return bubble is not null && TryActivateAgentOverlay(bubble, key);
    }

    internal bool DebugActivateMiniMapMarker(string id, Key key)
    {
        if (!miniMapMarkers.TryGetValue(id, out var marker))
        {
            return false;
        }

        var inputSource = PresentationSource.FromVisual(marker);
        if (inputSource is null)
        {
            return TryActivateMiniMapMarker(marker, key);
        }

        var args = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            inputSource,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        marker.RaiseEvent(args);
        return args.Handled;
    }

    internal void DebugSetCameraMode(string mode)
    {
        SetCameraMode(ParseCameraMode(mode));
    }

    internal void DebugReturnToSpeakerFollow()
    {
        ReturnToSpeakerFollow();
    }

    internal void DebugDismissInspector()
    {
        DismissInspector();
    }

    internal void DebugSetCinematicAutoCamera(bool enabled)
    {
        SetCinematicAutoCamera(enabled);
    }

    private void PopulateInspector()
    {
        var avatar = SelectedAvatar();
        if (avatar is null || inspectorDismissed)
        {
            AgentInspectorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var accent = AccentColor(avatar);
        AgentInspectorPanel.Visibility = Visibility.Visible;
        AgentInspectorPanel.BorderBrush = BrushFrom(Color.FromArgb(235, accent.R, accent.G, accent.B));
        InspectorNameText.Text = avatar.Name;
        InspectorModelText.Text = string.IsNullOrWhiteSpace(avatar.Model) ? "model inherited from provider" : avatar.Model;
        InspectorRoleText.Text = $"Role: {DisplayStatus(avatar.Status)} | Voice: {DisplayWorldValue(avatar.VoiceStyle)} | Pressure: {DisplayWorldValue(avatar.PressureProfile)}{(avatar.Locked ? " | Locked" : "")} | {FallbackText(avatar.Persona, "No persona set.")}";
        InspectorLastMessageText.Text = avatar.LastMessageTurn > 0
            ? $"Last turn {avatar.LastMessageTurn} [{MessageKindStatus(avatar)}]: {avatar.LastMessageText}\nTokens: {avatar.PromptTokens} prompt / {avatar.CompletionTokens} completion / {avatar.TotalTokens} total"
            : "Last message: none yet.";
        InspectorNotesText.Text = $"Public: {avatar.PublicNotesSummary}\nPrivate: {avatar.PrivateNotesSummary}";
        InspectorEventItems.Children.Clear();
        foreach (var label in EventLabels(avatar))
        {
            InspectorEventItems.Children.Add(EventChip(label, accent));
        }
    }

    private TextBlock EventChip(string label, Color accent)
    {
        return new TextBlock
        {
            Text = label,
            Foreground = BrushFrom(Blend(accent, Colors.White, 0.24)),
            Background = BrushFrom(Color.FromArgb(190, 12, 19, 17)),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(6, 2, 6, 3),
            Margin = new Thickness(0, 0, 6, 4)
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (observeSystemMotionPreferences && !systemMotionPreferenceSubscribed)
        {
            SystemMotionPreferences.PreferenceChanged += OnSystemMotionPreferenceChanged;
            systemMotionPreferenceSubscribed = true;
        }

        UpdateAnimationState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (systemMotionPreferenceSubscribed)
        {
            SystemMotionPreferences.PreferenceChanged -= OnSystemMotionPreferenceChanged;
            systemMotionPreferenceSubscribed = false;
        }

        StopAnimation();
    }

    private void OnSystemMotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SystemMotionPreferences.IsAnimationPreferenceChange(e.PropertyName))
        {
            return;
        }

        UpdateMotionAutomationState();
        UpdateAnimationState();
    }

    private void UpdateMotionAutomationState()
    {
        AutomationProperties.SetItemStatus(
            WorldRoot,
            animationsEnabledProvider() ? "animation enabled" : "reduced motion");
    }

    internal static AgentWorldRenderPolicy ResolveRenderPolicy(
        bool isLoaded,
        bool isVisible,
        bool hasAvatars,
        bool animationsEnabled)
    {
        var canRender = isLoaded && isVisible && hasAvatars;
        return new AgentWorldRenderPolicy(
            RunContinuousAnimation: canRender && animationsEnabled,
            RenderStableFrame: canRender && !animationsEnabled);
    }

    private void UpdateAnimationState()
    {
        var policy = ResolveRenderPolicy(
            IsLoaded,
            IsVisible,
            currentWorld?.Avatars.Count > 0,
            animationsEnabledProvider());
        if (policy.RunContinuousAnimation && !animationTimer.IsEnabled)
        {
            StartAnimation();
        }
        else if (!policy.RunContinuousAnimation && animationTimer.IsEnabled)
        {
            StopAnimation();
        }

        if (policy.RenderStableFrame)
        {
            RenderStableWorldState();
        }
    }

    private void StartAnimation()
    {
        animationClock.Restart();
        animationTimer.Start();
    }

    private void StopAnimation()
    {
        animationTimer.Stop();
        animationClock.Reset();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var deltaSeconds = animationClock.Elapsed.TotalSeconds;
        animationClock.Restart();
        if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
        {
            deltaSeconds = animationTimer.Interval.TotalSeconds;
        }

        elapsedSeconds += Math.Min(deltaSeconds, MaxAnimationStepSeconds);
        AnimateAgents();
        UpdateCamera(immediate: false);
        PositionOverlays();
        PositionMiniMap();
    }

    private void RenderStableWorldState()
    {
        AnimateAgents(stablePose: true);
        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
    }

    internal void DebugAdvanceWorld(double seconds)
    {
        elapsedSeconds += seconds;
        AnimateAgents();
        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
    }

    internal void DebugRenderStableWorldState()
    {
        RenderStableWorldState();
    }

    internal void DebugOrbitCamera(double deltaX, double deltaY)
    {
        OrbitCamera(deltaX, deltaY);
        if (cameraMode != AgentWorldCameraMode.Free)
        {
            SetCameraMode(AgentWorldCameraMode.Free);
        }

        UpdateCamera(immediate: true);
        PositionMiniMap();
    }

    internal void DebugPanCamera(double deltaX, double deltaY)
    {
        PanCamera(deltaX, deltaY);
        if (cameraMode != AgentWorldCameraMode.Free)
        {
            SetCameraMode(AgentWorldCameraMode.Free);
        }

        UpdateCamera(immediate: true);
        PositionMiniMap();
    }

    internal void DebugZoomCamera(double wheelDelta)
    {
        ZoomCamera(wheelDelta);
        UpdateCamera(immediate: true);
        PositionMiniMap();
    }

    internal bool DebugPressWorldKey(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        return HandleWorldKey(key, modifiers);
    }

    private void AnimateAgents(bool stablePose = false)
    {
        var speakerIndex = -1;
        var listenerSlots = 0;
        for (var index = 0; index < agentVisuals.Count; index++)
        {
            if (speakerIndex < 0 && agentVisuals[index].Avatar.Speaking)
            {
                speakerIndex = index;
            }
        }

        var sampleSeconds = stablePose ? 0 : elapsedSeconds;
        var dt = stablePose
            ? 1.5
            : Math.Clamp(elapsedSeconds - lastAnimateElapsed, 0, 1.5);
        lastAnimateElapsed = elapsedSeconds;
        // Critically-damped follow toward each agent's goal so travel reads as deliberate
        // walking rather than a teleport, even when the speaker changes between snapshots.
        var followFactor = stablePose ? 1 : 1 - Math.Exp(-dt * 1.9);

        // Wash the stage in the active speaker's accent so each turn reads as an event.
        var targetLightColor = speakerIndex >= 0
            ? Blend(NeutralStageLightColor, agentVisuals[speakerIndex].Accent, 0.55)
            : NeutralStageLightColor;
        speakerAccentLight.Color = Blend(speakerAccentLight.Color, targetLightColor, stablePose ? 1 : Math.Clamp(followFactor, 0.04, 1));

        EnsureAnimationBuffers(agentVisuals.Count);
        var positions = animationPositions;
        var nextPositions = animationNextPositions;
        var phases = animationPhases;
        for (var index = 0; index < agentVisuals.Count; index++)
        {
            var visual = agentVisuals[index];
            var avatar = visual.Avatar;
            phases[index] = avatar.MotionPhase;
            var current = new Point(visual.Translate.OffsetX, visual.Translate.OffsetZ);
            var listenerSlot = speakerIndex >= 0 && index != speakerIndex ? listenerSlots++ : 0;
            var goal = GoalPosition(avatar, index, speakerIndex, listenerSlot, agentVisuals.Count, sampleSeconds);
            var eased = stablePose
                ? goal
                : new Point(
                    current.X + ((goal.X - current.X) * followFactor),
                    current.Y + ((goal.Y - current.Y) * followFactor));
            positions[index] = ClampWorldPoint(eased);

            // Predict a short step further toward the goal for heading; near zero when settled.
            var toGoalX = goal.X - eased.X;
            var toGoalZ = goal.Y - eased.Y;
            var remaining = Math.Sqrt((toGoalX * toGoalX) + (toGoalZ * toGoalZ));
            if (remaining > 0.02)
            {
                var step = Math.Min(0.18, remaining);
                nextPositions[index] = ClampWorldPoint(new Point(
                    eased.X + (toGoalX / remaining * step),
                    eased.Y + (toGoalZ / remaining * step)));
            }
            else
            {
                nextPositions[index] = positions[index];
            }
        }

        ResolveAgentCollisions(positions, phases);
        ResolveObstacleCollisions(positions);
        ResolveAgentCollisions(nextPositions, phases);
        ResolveObstacleCollisions(nextPositions);

        for (var index = 0; index < agentVisuals.Count; index++)
        {
            var visual = agentVisuals[index];
            var avatar = visual.Avatar;
            var phase = avatar.MotionPhase;
            var position = positions[index];
            var next = nextPositions[index];
            var deltaX = next.X - position.X;
            var deltaZ = next.Y - position.Y;
            var idleBob = stablePose ? 0 : Math.Max(0, Math.Sin((elapsedSeconds * 4.2) + phase) * 0.032);
            var jumpBeat = stablePose ? 0 : Math.Max(0, Math.Sin((elapsedSeconds * 5.4) + phase));
            var jump = stablePose ? 0 : avatar.Speaking ? Math.Pow(jumpBeat, 1.55) * 0.34 : idleBob;
            var pulse = stablePose ? 1 : 1 + (Math.Sin((elapsedSeconds * 3.4) + phase) * (avatar.Speaking ? 0.035 : 0.012));
            var shadowScale = Math.Clamp(1.08 - (jump * 0.62), 0.78, 1.08);

            visual.Translate.OffsetX = position.X;
            visual.Translate.OffsetY = jump + (avatar.Speaking ? idleBob * 0.35 : 0);
            visual.Translate.OffsetZ = position.Y;
            visual.ShadowTranslate.OffsetX = position.X;
            visual.ShadowTranslate.OffsetZ = position.Y;
            visual.ShadowScale.ScaleX = shadowScale * (avatar.Speaking ? 1.08 : 1);
            visual.ShadowScale.ScaleY = 1;
            visual.ShadowScale.ScaleZ = shadowScale;
            var walkingFacing = ToDegrees(Math.Atan2(deltaX, deltaZ));
            var focusFacing = ListenerFocusFacing(index, speakerIndex, positions);
            var speakingFacing = ArenaFocusFacing(position);
            var facing = avatar.Speaking && Distance(position, new Point()) > 0.2
                ? speakingFacing
                : focusFacing ?? walkingFacing;
            visual.Rotate.Angle = stablePose
                ? facing
                : facing + (Math.Sin((elapsedSeconds * 1.2) + phase) * (avatar.Speaking ? 2.5 : 4));
            visual.Scale.ScaleX = pulse;
            visual.Scale.ScaleY = avatar.Speaking ? 1.04 + ((pulse - 1) * 0.5) : 1;
            visual.Scale.ScaleZ = pulse;
            var attentionPulse = stablePose ? 1 : 1 + (Math.Sin((elapsedSeconds * 5.2) + phase) * (avatar.Speaking ? 0.08 : 0.025));
            visual.AttentionHaloScale.ScaleX = attentionPulse;
            visual.AttentionHaloScale.ScaleY = attentionPulse;
            if (stablePose)
            {
                SetStableGesturePose(visual);
            }
            else
            {
                AnimateGesture(visual, elapsedSeconds);
            }
        }
    }

    private void EnsureAnimationBuffers(int count)
    {
        if (animationPositions.Length == count)
        {
            return;
        }

        animationPositions = new Point[count];
        animationNextPositions = new Point[count];
        animationPhases = new double[count];
        animationBufferResizeCount++;
    }

    private static double? ListenerFocusFacing(int index, int speakerIndex, IReadOnlyList<Point> positions)
    {
        if (speakerIndex < 0 || index == speakerIndex || speakerIndex >= positions.Count || index >= positions.Count)
        {
            return null;
        }

        var source = positions[index];
        var speaker = positions[speakerIndex];
        if (Distance(source, speaker) < 0.2)
        {
            return null;
        }

        return ToDegrees(Math.Atan2(speaker.X - source.X, speaker.Y - source.Y));
    }

    private static double ArenaFocusFacing(Point position)
    {
        return ToDegrees(Math.Atan2(-position.X, -position.Y));
    }

    private static Point GoalPosition(AgentWorldAvatar avatar, int index, int speakerIndex, int listenerSlot, int agentCount, double seconds)
    {
        // Gentle drift keeps settled agents alive without looking like they wander off.
        var driftX = Math.Sin((seconds * 0.5) + avatar.MotionPhase) * 0.12;
        var driftZ = Math.Cos((seconds * 0.43) + avatar.MotionPhase) * 0.1;

        if (speakerIndex >= 0 && index == speakerIndex)
        {
            // Take the podium: stand at the front edge of the central dais facing the room.
            return ClampWorldPoint(new Point(driftX * 0.5, 1.85 + (driftZ * 0.3)));
        }

        if (speakerIndex >= 0)
        {
            // Listeners gather in a loose arc around the dais, all oriented toward it.
            var spread = Math.PI * 1.25;
            var slots = Math.Max(1, agentCount - 1);
            var fraction = slots <= 1 ? 0.5 : (double)listenerSlot / (slots - 1);
            var angle = (Math.PI / 2) + (spread * (fraction - 0.5));
            var radius = 3.15;
            return ClampWorldPoint(new Point(
                (Math.Cos(angle) * radius) + driftX,
                (Math.Sin(angle) * radius) + 0.4 + driftZ));
        }

        // Idle: settle at a stable perimeter station so the ready room looks composed.
        var stationAngle = (2 * Math.PI * index / Math.Max(1, agentCount)) + 0.6;
        return ClampWorldPoint(new Point(
            (Math.Cos(stationAngle) * 5.4) + driftX,
            (Math.Sin(stationAngle) * 3.7) + driftZ));
    }

    private static void ResolveObstacleCollisions(Point[] positions)
    {
        foreach (var (obstacleX, obstacleZ, obstacleRadius) in WorldObstacles)
        {
            var minDistance = obstacleRadius + AgentCollisionRadius;
            for (var index = 0; index < positions.Length; index++)
            {
                var deltaX = positions[index].X - obstacleX;
                var deltaZ = positions[index].Y - obstacleZ;
                var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
                if (distance >= minDistance)
                {
                    continue;
                }

                if (distance < 0.0001)
                {
                    deltaX = 1;
                    deltaZ = 0;
                    distance = 1;
                }

                positions[index] = ClampWorldPoint(new Point(
                    obstacleX + (deltaX / distance * minDistance),
                    obstacleZ + (deltaZ / distance * minDistance)));
            }
        }
    }

    private static void ResolveAgentCollisions(Point[] positions, IReadOnlyList<double> phases)
    {
        if (positions.Length < 2)
        {
            return;
        }

        var minimumDistance = AgentCollisionRadius * 2;
        var minimumDistanceSquared = minimumDistance * minimumDistance;
        var iterationCount = Math.Clamp(8 + (positions.Length * 2), 8, 40);
        for (var iteration = 0; iteration < iterationCount; iteration++)
        {
            var moved = false;
            for (var firstIndex = 0; firstIndex < positions.Length; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < positions.Length; secondIndex++)
                {
                    var first = positions[firstIndex];
                    var second = positions[secondIndex];
                    var deltaX = second.X - first.X;
                    var deltaY = second.Y - first.Y;
                    var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                    if (distanceSquared >= minimumDistanceSquared)
                    {
                        continue;
                    }

                    var distance = Math.Sqrt(distanceSquared);
                    var normalDistance = distance;
                    if (normalDistance < 0.0001)
                    {
                        var angle = phases[firstIndex] - phases[secondIndex] + firstIndex + secondIndex + 1;
                        deltaX = Math.Cos(angle);
                        deltaY = Math.Sin(angle);
                        normalDistance = 1;
                        distance = 0;
                    }

                    var push = (minimumDistance - distance) * 0.5;
                    var normalX = deltaX / normalDistance;
                    var normalY = deltaY / normalDistance;
                    positions[firstIndex] = ClampWorldPoint(new Point(first.X - (normalX * push), first.Y - (normalY * push)));
                    positions[secondIndex] = ClampWorldPoint(new Point(second.X + (normalX * push), second.Y + (normalY * push)));
                    moved = true;
                }
            }

            if (!moved)
            {
                return;
            }
        }
    }

    private static void SetStableGesturePose(WorldAgentVisual visual)
    {
        var gesture = visual.Gesture;
        gesture.LeftLegSwing.Angle = 0;
        gesture.RightLegSwing.Angle = 0;
        gesture.LeftKneeBend.Angle = 5;
        gesture.RightKneeBend.Angle = 5;
        gesture.LeftFootPitch.Angle = -5;
        gesture.RightFootPitch.Angle = -5;
        gesture.BodyLean.Angle = 0;
        gesture.HeadNod.Angle = visual.Avatar.Speaking ? -3 : 0;
        if (!visual.Avatar.Speaking)
        {
            gesture.LeftArmSpread.Angle = -4;
            gesture.RightArmSpread.Angle = 4;
            gesture.LeftArmLift.Angle = 0;
            gesture.RightArmLift.Angle = 0;
            return;
        }

        switch (SpeakingGestureStyle(visual.Avatar))
        {
            case 0:
                gesture.LeftArmSpread.Angle = -78;
                gesture.RightArmSpread.Angle = 78;
                gesture.LeftArmLift.Angle = 16;
                gesture.RightArmLift.Angle = -12;
                break;
            case 1:
                gesture.LeftArmSpread.Angle = -96;
                gesture.RightArmSpread.Angle = 42;
                gesture.LeftArmLift.Angle = -12;
                gesture.RightArmLift.Angle = 24;
                break;
            case 2:
                gesture.LeftArmSpread.Angle = -50;
                gesture.RightArmSpread.Angle = 76;
                gesture.LeftArmLift.Angle = 22;
                gesture.RightArmLift.Angle = -28;
                break;
            default:
                gesture.LeftArmSpread.Angle = -64;
                gesture.RightArmSpread.Angle = 64;
                gesture.LeftArmLift.Angle = 30;
                gesture.RightArmLift.Angle = 30;
                break;
        }
    }

    private static void AnimateGesture(WorldAgentVisual visual, double seconds)
    {
        var avatar = visual.Avatar;
        var phase = avatar.MotionPhase;
        var gesture = visual.Gesture;
        AnimateLegs(gesture, seconds, phase, avatar.Speaking);
        if (!avatar.Speaking)
        {
            var idle = Math.Sin((seconds * 1.55) + phase) * 4;
            gesture.BodyLean.Angle = idle * 0.25;
            gesture.HeadNod.Angle = Math.Sin((seconds * 1.8) + phase) * 2.5;
            gesture.LeftArmSpread.Angle = -4 + idle;
            gesture.RightArmSpread.Angle = 4 - idle;
            gesture.LeftArmLift.Angle = 0;
            gesture.RightArmLift.Angle = 0;
            return;
        }

        var primary = Math.Sin((seconds * 8.2) + phase);
        var secondary = Math.Sin((seconds * 5.7) + (phase * 0.73));
        var beat = Math.Abs(primary);
        var style = SpeakingGestureStyle(avatar);

        gesture.BodyLean.Angle = (secondary * 7) + (style == 2 ? primary * 5 : 0);
        gesture.HeadNod.Angle = -6 + (beat * 13);

        switch (style)
        {
            case 0:
                gesture.LeftArmSpread.Angle = -78 - (primary * 20);
                gesture.RightArmSpread.Angle = 78 + (secondary * 20);
                gesture.LeftArmLift.Angle = 10 + (secondary * 22);
                gesture.RightArmLift.Angle = -8 - (primary * 22);
                break;
            case 1:
                gesture.LeftArmSpread.Angle = -105 + (secondary * 12);
                gesture.RightArmSpread.Angle = 36 + (primary * 18);
                gesture.LeftArmLift.Angle = -10 - (beat * 20);
                gesture.RightArmLift.Angle = 22 + (secondary * 24);
                break;
            case 2:
                gesture.LeftArmSpread.Angle = -42 - (beat * 18);
                gesture.RightArmSpread.Angle = 92 - (beat * 28);
                gesture.LeftArmLift.Angle = 34 * primary;
                gesture.RightArmLift.Angle = -46 * primary;
                break;
            default:
                gesture.LeftArmSpread.Angle = -62 + (primary * 24);
                gesture.RightArmSpread.Angle = 62 - (primary * 24);
                gesture.LeftArmLift.Angle = 28 + (secondary * 16);
                gesture.RightArmLift.Angle = 28 - (secondary * 16);
                break;
        }
    }

    private static void AnimateLegs(AgentGestureRig gesture, double seconds, double phase, bool speaking)
    {
        var stride = Math.Sin((seconds * (speaking ? 7.2 : 4.8)) + phase);
        var counterStride = -stride;
        var leftLift = Math.Max(0, stride);
        var rightLift = Math.Max(0, counterStride);
        var bounce = Math.Abs(Math.Sin((seconds * (speaking ? 8.4 : 4.8)) + phase));
        var swing = speaking ? 22 : 13;
        var kneeBase = speaking ? 10 : 5;
        var kneeLift = speaking ? 22 : 14;
        var footLift = speaking ? 16 : 9;

        gesture.LeftLegSwing.Angle = stride * swing;
        gesture.RightLegSwing.Angle = counterStride * swing;
        gesture.LeftKneeBend.Angle = kneeBase + (rightLift * kneeLift) + (bounce * (speaking ? 7 : 3));
        gesture.RightKneeBend.Angle = kneeBase + (leftLift * kneeLift) + (bounce * (speaking ? 7 : 3));
        gesture.LeftFootPitch.Angle = -5 + (leftLift * footLift) - (rightLift * 4);
        gesture.RightFootPitch.Angle = -5 + (rightLift * footLift) - (leftLift * 4);
    }

    private void UpdateCamera(bool immediate)
    {
        var focusedFollowVisual = cameraMode == AgentWorldCameraMode.FollowSpeaker
            ? FocusedFollowVisual()
            : null;
        var desiredTarget = DesiredCameraTarget(focusedFollowVisual);
        if (immediate)
        {
            cameraTarget = desiredTarget;
        }
        else
        {
            cameraTarget = Lerp(cameraTarget, desiredTarget, 0.085);
        }

        var activeYaw = cinematicAutoCamera && cameraMode != AgentWorldCameraMode.Free
            ? cameraYaw + (elapsedSeconds * 0.11)
            : cameraYaw;
        var activeDistance = cameraMode == AgentWorldCameraMode.Overview
            ? Math.Max(cameraDistance, 20)
            : cameraDistance;
        var activePitch = cameraMode == AgentWorldCameraMode.Overview
            ? Math.Max(cameraPitch, 0.96)
            : cameraPitch;
        var horizontalDistance = Math.Cos(activePitch) * activeDistance;
        var cameraPosition = new Point3D(
            cameraTarget.X + (Math.Sin(activeYaw) * horizontalDistance),
            cameraTarget.Y + (Math.Sin(activePitch) * activeDistance),
            cameraTarget.Z + (Math.Cos(activeYaw) * horizontalDistance));
        camera.Position = cameraPosition;
        camera.LookDirection = cameraTarget - cameraPosition;
        camera.UpDirection = new Vector3D(0, 1, 0);
        var badgeLabel = cameraMode switch
        {
            AgentWorldCameraMode.FollowSpeaker when focusedFollowVisual is { } focused => focused.FollowBadgeLabel,
            AgentWorldCameraMode.Free => "FREE CAMERA",
            AgentWorldCameraMode.Overview => "OVERVIEW",
            _ => "EXPLORE WORLD"
        };
        if (!worldBadgeLabel.Equals(badgeLabel, StringComparison.Ordinal))
        {
            worldBadgeLabel = badgeLabel;
            WorldBadgeText.Text = badgeLabel;
        }
    }

    private Point3D DesiredCameraTarget(WorldAgentVisual? focusedFollowVisual)
    {
        if (cameraMode == AgentWorldCameraMode.Free)
        {
            return cameraTarget;
        }

        if (cameraMode == AgentWorldCameraMode.Overview)
        {
            return new Point3D(manualPan.X * 0.25, 0.92, manualPan.Z * 0.25);
        }

        if (focusedFollowVisual is not null)
        {
            return new Point3D(
                focusedFollowVisual.Translate.OffsetX + manualPan.X,
                0.8,
                focusedFollowVisual.Translate.OffsetZ + manualPan.Z);
        }

        return new Point3D(manualPan.X, 0.72, manualPan.Z);
    }

    private WorldAgentVisual? FocusedFollowVisual()
    {
        return manualSelectionPinned
            ? SelectedVisual() ?? CurrentSpeakerVisual()
            : CurrentSpeakerVisual() ?? SelectedVisual();
    }

    private WorldAgentVisual? CurrentSpeakerVisual()
    {
        WorldAgentVisual? current = null;
        foreach (var visual in agentVisuals)
        {
            if (!visual.Avatar.Speaking)
            {
                continue;
            }

            if (current is null || visual.Avatar.BubbleTurn > current.Avatar.BubbleTurn)
            {
                current = visual;
            }
        }

        return current;
    }

    private void OnWorldMouseButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveOverlay(e.OriginalSource as DependencyObject))
        {
            return;
        }

        WorldRoot.Focus();
        lastMousePoint = e.GetPosition(WorldRoot);
        dragStartPoint = lastMousePoint;
        dragMoved = false;
        dragMode = e.ChangedButton == MouseButton.Right ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? CameraDragMode.Pan
                : CameraDragMode.Orbit;
        WorldRoot.CaptureMouse();
        e.Handled = true;
    }

    private void OnWorldMouseMove(object sender, MouseEventArgs e)
    {
        if (dragMode == CameraDragMode.None || lastMousePoint is not { } last)
        {
            return;
        }

        var current = e.GetPosition(WorldRoot);
        var deltaX = current.X - last.X;
        var deltaY = current.Y - last.Y;
        if (dragStartPoint is { } start && Distance(start, current) > 6)
        {
            dragMoved = true;
        }

        if (dragMode == CameraDragMode.Pan)
        {
            PanCamera(deltaX, deltaY);
        }
        else
        {
            OrbitCamera(deltaX, deltaY);
        }

        lastMousePoint = current;
        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
        e.Handled = true;
    }

    private void OnWorldMouseButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (dragMode == CameraDragMode.None)
        {
            return;
        }

        if (!dragMoved && e.ChangedButton == MouseButton.Left)
        {
            SelectNearestAgent(e.GetPosition(WorldRoot));
        }

        dragMode = CameraDragMode.None;
        lastMousePoint = null;
        dragStartPoint = null;
        WorldRoot.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnWorldMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ShouldIgnoreWorldWheel(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        ZoomCamera(e.Delta);
        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
        e.Handled = true;
    }

    private void OnWorldMouseLeave(object sender, MouseEventArgs e)
    {
        if (dragMode == CameraDragMode.None)
        {
            return;
        }

        dragMode = CameraDragMode.None;
        lastMousePoint = null;
        dragStartPoint = null;
        WorldRoot.ReleaseMouseCapture();
    }

    private void OnWorldKeyDown(object sender, KeyEventArgs e)
    {
        if (IsInteractiveOverlay(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (HandleWorldKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    private bool HandleWorldKey(Key key, ModifierKeys modifiers)
    {
        var handled = true;
        switch (key)
        {
            case Key.F:
            case Key.Home:
                ReturnToSpeakerFollow();
                break;
            case Key.O:
                SetCameraMode(AgentWorldCameraMode.Overview);
                break;
            case Key.R:
                ResetCameraView();
                break;
            case Key.N:
                handled = CycleSelectedAgent(1);
                break;
            case Key.P:
                handled = CycleSelectedAgent(-1);
                break;
            case Key.C:
                SetCinematicAutoCamera(!cinematicAutoCamera);
                break;
            case Key.Escape when AgentInspectorPanel.Visibility == Visibility.Visible:
                DismissInspector();
                break;
            case Key.Left when modifiers.HasFlag(ModifierKeys.Shift):
                PanCamera(-36, 0);
                break;
            case Key.Right when modifiers.HasFlag(ModifierKeys.Shift):
                PanCamera(36, 0);
                break;
            case Key.Up when modifiers.HasFlag(ModifierKeys.Shift):
                PanCamera(0, -36);
                break;
            case Key.Down when modifiers.HasFlag(ModifierKeys.Shift):
                PanCamera(0, 36);
                break;
            case Key.Left:
                OrbitCamera(36, 0);
                break;
            case Key.Right:
                OrbitCamera(-36, 0);
                break;
            case Key.Up:
                OrbitCamera(0, -30);
                break;
            case Key.Down:
                OrbitCamera(0, 30);
                break;
            case Key.Add:
            case Key.OemPlus:
                ZoomCamera(120);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomCamera(-120);
                break;
            default:
                handled = false;
                break;
        }

        if (!handled)
        {
            return false;
        }

        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
        return true;
    }

    private void OnMiniMapMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Ellipse)
        {
            return;
        }

        HandleMiniMapClick(e.GetPosition(WorldMiniMapCanvas));
        e.Handled = true;
    }

    private void HandleMiniMapClick(Point clickPoint)
    {
        if (TrySelectNearestMiniMapAgent(clickPoint))
        {
            return;
        }

        FocusMiniMapPoint(clickPoint);
    }

    private void OrbitCamera(double deltaX, double deltaY)
    {
        if (cameraMode != AgentWorldCameraMode.Free)
        {
            SetCameraMode(AgentWorldCameraMode.Free);
        }

        cameraYaw -= deltaX * 0.008;
        cameraPitch = Math.Clamp(cameraPitch + (deltaY * 0.006), 0.24, 1.08);
    }

    private void PanCamera(double deltaX, double deltaY)
    {
        var right = new Vector3D(Math.Cos(cameraYaw), 0, -Math.Sin(cameraYaw));
        var forward = new Vector3D(Math.Sin(cameraYaw), 0, Math.Cos(cameraYaw));
        var amount = cameraDistance * 0.0018;
        var pan = (-right * (deltaX * amount)) + (forward * (deltaY * amount));
        if (cameraMode == AgentWorldCameraMode.Free)
        {
            cameraTarget += pan;
            cameraTarget.X = Math.Clamp(cameraTarget.X, -WorldLimitX, WorldLimitX);
            cameraTarget.Z = Math.Clamp(cameraTarget.Z, -WorldLimitZ, WorldLimitZ);
        }
        else
        {
            manualPan += pan;
            manualPan.X = Math.Clamp(manualPan.X, -WorldLimitX, WorldLimitX);
            manualPan.Z = Math.Clamp(manualPan.Z, -WorldLimitZ, WorldLimitZ);
        }
    }

    private void ZoomCamera(double wheelDelta)
    {
        cameraDistance = Math.Clamp(cameraDistance - (wheelDelta * 0.006), 5.2, 24);
    }

    private void PositionMiniMap()
    {
        if (currentWorld is null || agentVisuals.Count == 0)
        {
            ClearMiniMapMarkers();
            WorldMiniMapPanel.Visibility = Visibility.Collapsed;
            return;
        }

        WorldMiniMapPanel.Visibility = Visibility.Visible;
        EnsureMiniMapFrame();
        if (miniMapStyleDirty)
        {
            UpdateMiniMapFrameStyle();
            miniMapStyleDirty = false;
        }

        if (miniMapRosterDirty)
        {
            SynchronizeMiniMapRoster();
            miniMapRosterDirty = false;
        }

        UpdateMiniMapCameraTarget();

        foreach (var visual in agentVisuals)
        {
            UpdateMiniMapAgent(visual);
        }
    }

    private void EnsureMiniMapFrame()
    {
        EnsureMiniMapFrameElement(miniMapBounds, MiniMapLeft, MiniMapTop);
        EnsureMiniMapFrameElement(miniMapVerticalAxis);
        EnsureMiniMapFrameElement(miniMapHorizontalAxis);
        EnsureMiniMapCameraTargetElement();
    }

    private void EnsureMiniMapFrameElement(UIElement element, double? left = null, double? top = null)
    {
        Canvas.SetZIndex(element, 0);
        if (left is not null)
        {
            Canvas.SetLeft(element, left.Value);
        }

        if (top is not null)
        {
            Canvas.SetTop(element, top.Value);
        }

        if (!WorldMiniMapCanvas.Children.Contains(element))
        {
            WorldMiniMapCanvas.Children.Add(element);
        }
    }

    private void UpdateMiniMapFrameStyle()
    {
        miniMapBounds.Stroke = BrushFrom(ResourceColor("DisabledBorderBrush", Color.FromRgb(39, 52, 47)));
        var axisStroke = BrushFrom(ResourceColor("ControlBorderBrush", Color.FromRgb(72, 100, 90)), 0.45);
        miniMapVerticalAxis.Stroke = axisStroke;
        miniMapHorizontalAxis.Stroke = axisStroke;
        var accent = ResourceColor("TextBrush", Color.FromRgb(221, 231, 226));
        var targetStroke = BrushFrom(Color.FromArgb(190, accent.R, accent.G, accent.B));
        miniMapCameraTargetMarker.BorderBrush = targetStroke;
        if (miniMapCameraTargetMarker.Child is Grid grid)
        {
            foreach (var bar in grid.Children.OfType<Border>())
            {
                bar.Background = targetStroke;
            }
        }
    }

    private void ClearMiniMapMarkers()
    {
        foreach (var marker in miniMapMarkers.Values)
        {
            WorldMiniMapCanvas.Children.Remove(marker);
        }

        miniMapCameraTargetMarker.Visibility = Visibility.Collapsed;
        miniMapMarkers.Clear();
        miniMapMarkerStates.Clear();
        activeMiniMapAgentIds.Clear();
        inactiveMiniMapMarkerIds.Clear();
        miniMapRosterDirty = true;
    }

    private void EnsureMiniMapCameraTargetElement()
    {
        if (miniMapCameraTargetMarker.Child is null)
        {
            miniMapCameraTargetMarker.Child = CreateMiniMapTargetGlyph();
        }

        if (!WorldMiniMapCanvas.Children.Contains(miniMapCameraTargetMarker))
        {
            WorldMiniMapCanvas.Children.Add(miniMapCameraTargetMarker);
        }
    }

    private Grid CreateMiniMapTargetGlyph()
    {
        var grid = new Grid
        {
            Width = 14,
            Height = 14,
            IsHitTestVisible = false
        };
        var horizontal = new Border
        {
            Height = 2,
            Width = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var vertical = new Border
        {
            Width = 2,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(horizontal);
        grid.Children.Add(vertical);
        return grid;
    }

    private void UpdateMiniMapCameraTarget()
    {
        var placement = CalculateMiniMapTargetPlacement(cameraTarget.X, cameraTarget.Z);
        miniMapCameraTargetMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(miniMapCameraTargetMarker, placement.Left);
        Canvas.SetTop(miniMapCameraTargetMarker, placement.Top);
        Canvas.SetZIndex(miniMapCameraTargetMarker, 3);
    }

    private void SynchronizeMiniMapRoster()
    {
        activeMiniMapAgentIds.Clear();
        foreach (var visual in agentVisuals)
        {
            activeMiniMapAgentIds.Add(visual.Avatar.Id);
        }

        inactiveMiniMapMarkerIds.Clear();
        foreach (var id in miniMapMarkers.Keys)
        {
            if (!activeMiniMapAgentIds.Contains(id))
            {
                inactiveMiniMapMarkerIds.Add(id);
            }
        }

        foreach (var id in inactiveMiniMapMarkerIds)
        {
            var marker = miniMapMarkers[id];
            WorldMiniMapCanvas.Children.Remove(marker);
            miniMapMarkers.Remove(id);
            miniMapMarkerStates.Remove(id);
        }
    }

    private void UpdateMiniMapAgent(WorldAgentVisual visual)
    {
        var accent = visual.Accent;
        var selected = visual.Avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase);
        var marker = MiniMapMarkerFor(visual.Avatar.Id);
        var placement = CalculateMiniMapMarkerPlacement(visual.Translate.OffsetX, visual.Translate.OffsetZ, selected);

        marker.Width = placement.Width;
        marker.Height = placement.Height;
        var renderState = new MiniMapMarkerRenderState(
            accent,
            selected,
            visual.Avatar.Name,
            visual.Avatar.Speaking,
            visual.Avatar.Status);
        if (!miniMapMarkerStates.TryGetValue(visual.Avatar.Id, out var previousState) || previousState != renderState)
        {
            marker.Fill = BrushFrom(accent);
            marker.Stroke = BrushFrom(selected ? Colors.White : Blend(accent, Colors.Black, 0.45));
            marker.StrokeThickness = selected ? 2 : 1;
            marker.Tag = visual.Avatar.Id;
            marker.ToolTip = visual.Avatar.Name;
            AutomationProperties.SetName(marker, selected ? $"{visual.Avatar.Name} selected" : visual.Avatar.Name);
            AutomationProperties.SetHelpText(marker, selected
                ? "Selected agent marker. Activate to keep camera focused on this agent."
                : "Agent marker. Activate to select and focus this agent.");
            AutomationProperties.SetItemStatus(marker, selected
                ? "selected"
                : visual.Avatar.Speaking
                    ? "speaking"
                    : DisplayStatus(visual.Avatar.Status));
            miniMapMarkerStates[visual.Avatar.Id] = renderState;
        }

        Canvas.SetLeft(marker, placement.Left);
        Canvas.SetTop(marker, placement.Top);
        Canvas.SetZIndex(marker, selected ? 2 : 1);
    }

    private Ellipse MiniMapMarkerFor(string agentId)
    {
        if (miniMapMarkers.TryGetValue(agentId, out var marker))
        {
            if (!WorldMiniMapCanvas.Children.Contains(marker))
            {
                WorldMiniMapCanvas.Children.Add(marker);
            }

            return marker;
        }

        marker = new Ellipse
        {
            Cursor = Cursors.Hand,
            Focusable = true,
        };
        marker.MouseLeftButtonUp += AgentOverlay_MouseLeftButtonUp;
        marker.KeyDown += MiniMapMarker_KeyDown;
        miniMapMarkers[agentId] = marker;
        WorldMiniMapCanvas.Children.Add(marker);
        return marker;
    }

    private Point MiniMapPoint(double x, double z)
    {
        return new Point(
            MiniMapLeft + ((x + WorldLimitX) / (WorldLimitX * 2) * MiniMapWidth),
            MiniMapTop + MiniMapHeight - ((z + WorldLimitZ) / (WorldLimitZ * 2) * MiniMapHeight));
    }

    private static Point MiniMapWorldPoint(Point point)
    {
        var clampedX = Math.Clamp(point.X, MiniMapLeft, MiniMapLeft + MiniMapWidth);
        var clampedY = Math.Clamp(point.Y, MiniMapTop, MiniMapTop + MiniMapHeight);
        var normalizedX = (clampedX - MiniMapLeft) / MiniMapWidth;
        var normalizedZ = ((MiniMapTop + MiniMapHeight) - clampedY) / MiniMapHeight;
        return new Point(
            (normalizedX * WorldLimitX * 2) - WorldLimitX,
            (normalizedZ * WorldLimitZ * 2) - WorldLimitZ);
    }

    private static Rect CalculateMiniMapMarkerPlacement(double x, double z, bool selected)
    {
        var size = selected ? 11d : 8d;
        var radius = size / 2d;
        var point = new Point(
            MiniMapLeft + ((Math.Clamp(x, -WorldLimitX, WorldLimitX) + WorldLimitX) / (WorldLimitX * 2) * MiniMapWidth),
            MiniMapTop + MiniMapHeight - ((Math.Clamp(z, -WorldLimitZ, WorldLimitZ) + WorldLimitZ) / (WorldLimitZ * 2) * MiniMapHeight));
        var centerX = Math.Clamp(point.X, MiniMapLeft + radius, MiniMapLeft + MiniMapWidth - radius);
        var centerY = Math.Clamp(point.Y, MiniMapTop + radius, MiniMapTop + MiniMapHeight - radius);
        return new Rect(centerX - radius, centerY - radius, size, size);
    }

    private static Rect CalculateMiniMapTargetPlacement(double x, double z)
    {
        const double size = 14;
        const double radius = size / 2d;
        var point = new Point(
            MiniMapLeft + ((Math.Clamp(x, -WorldLimitX, WorldLimitX) + WorldLimitX) / (WorldLimitX * 2) * MiniMapWidth),
            MiniMapTop + MiniMapHeight - ((Math.Clamp(z, -WorldLimitZ, WorldLimitZ) + WorldLimitZ) / (WorldLimitZ * 2) * MiniMapHeight));
        var centerX = Math.Clamp(point.X, MiniMapLeft + radius, MiniMapLeft + MiniMapWidth - radius);
        var centerY = Math.Clamp(point.Y, MiniMapTop + radius, MiniMapTop + MiniMapHeight - radius);
        return new Rect(centerX - radius, centerY - radius, size, size);
    }

    private static Point ClampWorldPoint(Point point)
    {
        return new Point(
            Math.Clamp(point.X, -WorldLimitX, WorldLimitX),
            Math.Clamp(point.Y, -WorldLimitZ, WorldLimitZ));
    }

    private static int SpeakingGestureStyle(AgentWorldAvatar avatar)
    {
        return ((int)Math.Round(avatar.MotionPhase * 1000)) % 4;
    }

    private void PositionOverlays()
    {
        var width = OverlayCanvas.ActualWidth;
        var height = OverlayCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        foreach (var visual in agentVisuals)
        {
            PositionAttentionHalo(visual, width, height);

            var namePoint = ProjectToOverlay(AgentWorldPoint(visual, 0.1), width, height);
            if (namePoint is null)
            {
                visual.NameTag.Visibility = Visibility.Collapsed;
            }
            else
            {
                visual.NameTag.Visibility = Visibility.Visible;
                PlaceCentered(visual.NameTag, namePoint.Value.X, namePoint.Value.Y + 12, width, height);
            }

            if (visual.Avatar.Speaking)
            {
                UpdateBubbleState(visual);
                var bubblePoint = ProjectToOverlay(
                    AgentWorldPoint(visual, 1.42),
                    width,
                    height,
                    rejectOutsideViewport: false);
                if (bubblePoint is null)
                {
                    visual.Bubble.Visibility = Visibility.Collapsed;
                }
                else
                {
                    var bubbleAnchor = ClampBubbleAnchor(bubblePoint.Value, width, height);
                    visual.Bubble.Visibility = Visibility.Visible;
                    PlaceBubble(visual.Bubble, bubbleAnchor.X, bubbleAnchor.Y, width, height);
                }
            }
            else
            {
                visual.Bubble.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateBubbleState(WorldAgentVisual visual)
    {
        if (visual.Bubble.Tag is not BubbleChrome chrome)
        {
            return;
        }

        var selected = visual.Avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase);
        if (chrome.Selected != selected)
        {
            chrome.Selected = selected;
            chrome.Body.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            chrome.Body.Background = selected ? chrome.SelectedBodyBrush : chrome.BodyBrush;
            chrome.Body.BorderBrush = selected ? chrome.SelectedBorderBrush : chrome.BorderBrush;
            chrome.Pointer.Fill = chrome.Body.Background;
            chrome.Pointer.Stroke = chrome.Body.BorderBrush;
        }

        var automationStatus = selected ? "selected speaker" : "speaking";
        if (!chrome.AutomationStatus.Equals(automationStatus, StringComparison.Ordinal))
        {
            chrome.AutomationStatus = automationStatus;
            AutomationProperties.SetItemStatus(visual.Bubble, automationStatus);
        }
    }

    private void PositionAttentionHalo(WorldAgentVisual visual, double width, double height)
    {
        var selected = visual.Avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase);
        var highlighted = selected || visual.Avatar.Speaking;
        if (!highlighted)
        {
            visual.AttentionHalo.Visibility = Visibility.Collapsed;
            return;
        }

        var point = ProjectToOverlay(
            AgentWorldPoint(visual, 0.18),
            width,
            height,
            rejectOutsideViewport: false);
        if (point is null)
        {
            visual.AttentionHalo.Visibility = Visibility.Collapsed;
            return;
        }

        visual.AttentionHalo.Visibility = Visibility.Visible;
        visual.AttentionHalo.Opacity = selected && visual.Avatar.Speaking
            ? 0.98
            : selected ? 0.9 : 0.74;
        PlaceAttentionHalo(visual.AttentionHalo, point.Value, width, height);
        Canvas.SetZIndex(visual.AttentionHalo, selected ? 3 : 2);
        Canvas.SetZIndex(visual.NameTag, selected ? 5 : 4);
        Canvas.SetZIndex(visual.Bubble, visual.Avatar.Speaking ? 7 : 6);
    }

    private void SelectNearestAgent(Point clickPoint)
    {
        var width = OverlayCanvas.ActualWidth;
        var height = OverlayCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var nearest = agentVisuals
            .Select(visual =>
            {
                var projected = ProjectToOverlay(AgentWorldPoint(visual, 0.65), width, height);
                return new
                {
                    visual.Avatar.Id,
                    Distance = projected is null ? double.MaxValue : Distance(projected.Value, clickPoint)
                };
            })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (nearest is not null && nearest.Distance <= 52)
        {
            SelectAgent(nearest.Id);
        }
    }

    private bool TrySelectNearestMiniMapAgent(Point clickPoint)
    {
        if (agentVisuals.Count == 0)
        {
            return false;
        }

        var nearest = agentVisuals
            .Select(visual => new
            {
                visual.Avatar.Id,
                Distance = Distance(MiniMapPoint(visual.Translate.OffsetX, visual.Translate.OffsetZ), clickPoint)
            })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (nearest is not null && nearest.Distance <= 14)
        {
            SelectAgent(nearest.Id);
            return true;
        }

        return false;
    }

    private void FocusMiniMapPoint(Point clickPoint)
    {
        var worldPoint = MiniMapWorldPoint(clickPoint);
        if (cameraMode != AgentWorldCameraMode.Free)
        {
            SetCameraMode(AgentWorldCameraMode.Free);
        }

        cameraTarget = new Point3D(worldPoint.X, 0.72, worldPoint.Y);
        manualPan = new Vector3D(0, 0, 0);
        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
    }

    private void AgentOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && TryAgentOverlayId(element, out var id))
        {
            SelectAgent(id);
            e.Handled = true;
        }
    }

    private void AgentOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement element && TryActivateAgentOverlay(element, e.Key))
        {
            e.Handled = true;
        }
    }

    private bool TryActivateAgentOverlay(FrameworkElement element, Key key)
    {
        if (key is not (Key.Enter or Key.Return or Key.Space) ||
            !TryAgentOverlayId(element, out var id))
        {
            return false;
        }

        SelectAgent(id);
        return true;
    }

    private static bool TryAgentOverlayId(FrameworkElement element, out string id)
    {
        if (element.Tag is string tagId && !string.IsNullOrWhiteSpace(tagId))
        {
            id = tagId;
            return true;
        }

        if (element.DataContext is string contextId && !string.IsNullOrWhiteSpace(contextId))
        {
            id = contextId;
            return true;
        }

        id = "";
        return false;
    }

    private void MiniMapMarker_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement marker && TryActivateMiniMapMarker(marker, e.Key))
        {
            e.Handled = true;
        }
    }

    private bool TryActivateMiniMapMarker(FrameworkElement marker, Key key)
    {
        if (key is not (Key.Enter or Key.Return or Key.Space) ||
            marker.Tag is not string id)
        {
            return false;
        }

        SelectAgent(id);
        return true;
    }

    private void FollowCameraButton_Click(object sender, RoutedEventArgs e)
    {
        ReturnToSpeakerFollow();
    }

    private void FreeCameraButton_Click(object sender, RoutedEventArgs e)
    {
        SetCameraMode(AgentWorldCameraMode.Free);
    }

    private void OverviewCameraButton_Click(object sender, RoutedEventArgs e)
    {
        SetCameraMode(AgentWorldCameraMode.Overview);
    }

    private void CinematicCameraCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SetCinematicAutoCamera(CinematicCameraCheckBox.IsChecked == true);
    }

    private void InspectorCloseButton_Click(object sender, RoutedEventArgs e)
    {
        DismissInspector();
    }

    private void DismissInspector()
    {
        inspectorDismissed = true;
        AgentInspectorPanel.Visibility = Visibility.Collapsed;
        PopulateLegend();
        PositionMiniMap();
        PositionOverlays();
    }

    private void ReturnToSpeakerFollow()
    {
        manualSelectionPinned = false;
        if (CurrentSpeakerVisual() is { } speaker)
        {
            selectedAgentId = speaker.Avatar.Id;
        }

        PopulateLegend();
        PopulateInspector();
        PositionMiniMap();
        SetCameraMode(AgentWorldCameraMode.FollowSpeaker);
    }

    private void ResetCameraView()
    {
        manualSelectionPinned = false;
        manualPan = new Vector3D(0, 0, 0);
        cameraYaw = 0;
        cameraPitch = 0.56;
        cameraDistance = 12.2;
        hasPreOverviewCamera = false;
        if (CurrentSpeakerVisual() is { } speaker)
        {
            selectedAgentId = speaker.Avatar.Id;
        }

        PopulateLegend();
        PopulateInspector();
        SetCameraMode(AgentWorldCameraMode.FollowSpeaker);
    }

    private bool CycleSelectedAgent(int direction)
    {
        if (currentWorld is null || currentWorld.Avatars.Count == 0)
        {
            return false;
        }

        var avatars = currentWorld.Avatars;
        var currentIndex = IndexOfAvatar(avatars, selectedAgentId);
        if (currentIndex < 0 && FocusedFollowVisual() is { } focused)
        {
            currentIndex = IndexOfAvatar(avatars, focused.Avatar.Id);
        }

        if (currentIndex < 0)
        {
            currentIndex = direction >= 0 ? -1 : 0;
        }

        var nextIndex = Mod(currentIndex + direction, avatars.Count);
        SelectAgent(avatars[nextIndex].Id);
        return true;
    }

    private void SetCinematicAutoCamera(bool enabled)
    {
        cinematicAutoCamera = enabled;
        if (CinematicCameraCheckBox.IsChecked != enabled)
        {
            CinematicCameraCheckBox.IsChecked = enabled;
        }

        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
    }

    private void SetCameraMode(AgentWorldCameraMode mode)
    {
        var leavingOverview = cameraMode == AgentWorldCameraMode.Overview && mode != AgentWorldCameraMode.Overview;
        if (leavingOverview && hasPreOverviewCamera)
        {
            cameraDistance = preOverviewCameraDistance;
            cameraPitch = preOverviewCameraPitch;
            manualPan = preOverviewManualPan;
            hasPreOverviewCamera = false;
        }

        var enteringOverview = cameraMode != AgentWorldCameraMode.Overview && mode == AgentWorldCameraMode.Overview;
        if (enteringOverview)
        {
            preOverviewCameraDistance = cameraDistance;
            preOverviewCameraPitch = cameraPitch;
            preOverviewManualPan = manualPan;
            hasPreOverviewCamera = true;
        }

        cameraMode = mode;
        if (mode == AgentWorldCameraMode.Overview)
        {
            cameraDistance = Math.Max(cameraDistance, 20);
            cameraPitch = Math.Max(cameraPitch, 0.96);
            manualPan = new Vector3D(0, 0, 0);
        }

        UpdateCameraModeButtons();
        UpdateCamera(immediate: true);
        PositionOverlays();
        PositionMiniMap();
    }

    private void UpdateCameraModeButtons()
    {
        ApplyCameraButtonState(
            FollowCameraButton,
            cameraMode == AgentWorldCameraMode.FollowSpeaker,
            "Follow speaker camera",
            "Camera follows the current speaker. Shortcut F or Home.");
        ApplyCameraButtonState(
            FreeCameraButton,
            cameraMode == AgentWorldCameraMode.Free,
            "Free camera",
            "Camera stays where you move it. Arrow keys orbit, Shift plus arrow keys pan.");
        ApplyCameraButtonState(
            OverviewCameraButton,
            cameraMode == AgentWorldCameraMode.Overview,
            "Overview camera",
            "Camera shows the whole world. Shortcut O.");
    }

    private void UpdateHudLayout()
    {
        var width = WorldRoot.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var usableWidth = Math.Max(1, width - (HudSideMargin * 2));
        AgentInspectorPanel.Width = Math.Min(310, usableWidth);
        AgentInspectorPanel.Margin = new Thickness(HudSideMargin, width < 620 ? 136 : 96, HudSideMargin, 0);
        WorldBadge.MaxWidth = Math.Clamp(usableWidth * 0.45, 118, 220);
        WorldControlPanel.MaxWidth = usableWidth;
        WorldControlItems.MaxWidth = Math.Max(1, usableWidth - 16);
        WorldCuePanel.MaxWidth = Math.Clamp(usableWidth * (width < 620 ? 0.88 : 0.58), 220, 520);
        WorldCueItems.MaxWidth = Math.Max(1, WorldCuePanel.MaxWidth - 16);
    }

    private void ApplyCameraButtonState(Button button, bool active, string automationName, string automationHelp)
    {
        button.Background = active ? ResourceBrush("NavActiveBrush", Color.FromRgb(22, 54, 47)) : ResourceBrush("InputBrush", Color.FromRgb(13, 23, 20));
        button.BorderBrush = active ? ResourceBrush("PrimaryBorderBrush", Color.FromRgb(46, 168, 137)) : ResourceBrush("ControlBorderBrush", Color.FromRgb(72, 100, 90));
        button.Foreground = active ? ResourceBrush("TextBrush", Color.FromRgb(221, 231, 226)) : ResourceBrush("MutedTextBrush", Color.FromRgb(184, 199, 191));
        AutomationProperties.SetName(button, $"{automationName}, {(active ? "selected" : "not selected")}");
        AutomationProperties.SetHelpText(button, automationHelp);
        AutomationProperties.SetItemStatus(button, active ? "selected" : "not selected");
    }

    private static AgentWorldCameraMode ParseCameraMode(string mode)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "free" => AgentWorldCameraMode.Free,
            "overview" => AgentWorldCameraMode.Overview,
            _ => AgentWorldCameraMode.FollowSpeaker
        };
    }

    private static void PlaceCentered(FrameworkElement element, double centerX, double top, double canvasWidth, double canvasHeight)
    {
        element.Measure(new Size(Math.Min(280, Math.Max(120, canvasWidth - 32)), double.PositiveInfinity));
        var left = Math.Clamp(centerX - (element.DesiredSize.Width / 2), 10, Math.Max(10, canvasWidth - element.DesiredSize.Width - 10));
        var bottomLimit = Math.Max(50, canvasHeight - element.DesiredSize.Height - 10);
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, Math.Clamp(top, 50, bottomLimit));
    }

    private static void PlaceAttentionHalo(FrameworkElement element, Point anchor, double canvasWidth, double canvasHeight)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(1, element.DesiredSize.Width);
        var height = Math.Max(1, element.DesiredSize.Height);
        var left = Math.Clamp(anchor.X - (width / 2), 6, Math.Max(6, canvasWidth - width - 6));
        var top = Math.Clamp(anchor.Y - (height / 2), 50, Math.Max(50, canvasHeight - height - 8));
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }

    private static void PlaceBubble(FrameworkElement element, double centerX, double anchorY, double canvasWidth, double canvasHeight)
    {
        element.MaxWidth = Math.Max(1, Math.Min(270, canvasWidth - 20));
        element.Measure(new Size(Math.Min(280, Math.Max(1, canvasWidth - 20)), double.PositiveInfinity));
        var placement = CalculateBubblePlacement(element.DesiredSize, centerX, anchorY, canvasWidth, canvasHeight);
        ApplyBubblePointer(element, placement);
        Canvas.SetLeft(element, placement.Left);
        Canvas.SetTop(element, placement.Top);
    }

    private static BubblePlacement CalculateBubblePlacement(Size desiredSize, double centerX, double anchorY, double canvasWidth, double canvasHeight)
    {
        var bubbleWidth = Math.Max(1, Math.Min(desiredSize.Width, Math.Max(1, canvasWidth - 20)));
        var bubbleHeight = Math.Max(1, desiredSize.Height);
        const double sideMargin = 10;
        const double topMargin = 50;
        const double bottomMargin = 10;
        var pointerInset = Math.Min(16, bubbleWidth / 2);

        var maxLeft = Math.Max(sideMargin, canvasWidth - bubbleWidth - sideMargin);
        var left = Math.Clamp(centerX - (bubbleWidth / 2), sideMargin, maxLeft);
        var bottomLimit = Math.Max(topMargin, canvasHeight - bubbleHeight - bottomMargin);
        var idealAboveTop = anchorY - BubblePointerGap - bubbleHeight;
        var belowTop = anchorY + BubblePointerGap;
        var fitsBelow = belowTop <= bottomLimit;
        var placeBelow = idealAboveTop < topMargin && fitsBelow;
        var top = placeBelow
            ? Math.Clamp(belowTop, Math.Max(0, topMargin - BubbleTailHeight), bottomLimit)
            : Math.Clamp(idealAboveTop, topMargin, bottomLimit);
        var pointerX = Math.Clamp(centerX, left + pointerInset, left + bubbleWidth - pointerInset);
        var pointerY = placeBelow
            ? top
            : top + bubbleHeight;

        return new BubblePlacement(left, top, pointerX, pointerY, placeBelow);
    }

    private static Point ClampBubbleAnchor(Point projected, double canvasWidth, double canvasHeight)
    {
        const double sideInset = 12;
        const double topInset = 12;
        const double bottomInset = 12;
        return new Point(
            Math.Clamp(projected.X, sideInset, Math.Max(sideInset, canvasWidth - sideInset)),
            Math.Clamp(projected.Y, topInset, Math.Max(topInset, canvasHeight - bottomInset)));
    }

    private static void ApplyBubblePointer(FrameworkElement element, BubblePlacement placement)
    {
        if (element.Tag is not BubbleChrome chrome)
        {
            return;
        }

        Grid.SetRow(chrome.Body, placement.PlacedBelow ? 1 : 0);
        Grid.SetRow(chrome.Pointer, placement.PlacedBelow ? 0 : 1);
        chrome.Pointer.Points = placement.PlacedBelow
            ? new PointCollection
            {
                new(BubbleTailWidth / 2, 0),
                new(BubbleTailWidth, BubbleTailHeight),
                new(0, BubbleTailHeight)
            }
            : new PointCollection
            {
                new(0, 0),
                new(BubbleTailWidth, 0),
                new(BubbleTailWidth / 2, BubbleTailHeight)
            };

        var pointerLeft = placement.PointerX - placement.Left - (BubbleTailWidth / 2);
        var maxPointerLeft = Math.Max(0, element.DesiredSize.Width - BubbleTailWidth);
        chrome.PointerTransform.X = Math.Clamp(pointerLeft, 0, maxPointerLeft);
    }

    private Point? ProjectToOverlay(Point3D point, double width, double height, bool rejectOutsideViewport = true)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var forward = camera.LookDirection;
        if (forward.Length <= 0.0001)
        {
            return null;
        }

        forward.Normalize();
        var up = camera.UpDirection;
        if (up.Length <= 0.0001)
        {
            up = new Vector3D(0, 1, 0);
        }

        up.Normalize();
        var right = Vector3D.CrossProduct(forward, up);
        if (right.Length <= 0.0001)
        {
            right = new Vector3D(1, 0, 0);
        }

        right.Normalize();
        var trueUp = Vector3D.CrossProduct(right, forward);
        if (trueUp.Length <= 0.0001)
        {
            return null;
        }

        trueUp.Normalize();
        var toPoint = point - camera.Position;
        var depth = Vector3D.DotProduct(toPoint, forward);
        if (depth <= 0.08)
        {
            return null;
        }

        var halfFieldOfView = camera.FieldOfView * Math.PI / 360;
        var focalLength = (width / 2) / Math.Tan(halfFieldOfView);
        var projectedX = (width / 2) + (Vector3D.DotProduct(toPoint, right) * focalLength / depth);
        var projectedY = (height / 2) - (Vector3D.DotProduct(toPoint, trueUp) * focalLength / depth);
        if (double.IsNaN(projectedX) || double.IsInfinity(projectedX) ||
            double.IsNaN(projectedY) || double.IsInfinity(projectedY))
        {
            return null;
        }

        const double margin = 80;
        if (rejectOutsideViewport &&
            (projectedX < -margin || projectedX > width + margin ||
             projectedY < -margin || projectedY > height + margin))
        {
            return null;
        }

        return new Point(projectedX, projectedY);
    }

    private static Point3D AgentWorldPoint(WorldAgentVisual visual, double localY)
    {
        return new Point3D(visual.Translate.OffsetX, visual.Translate.OffsetY + localY, visual.Translate.OffsetZ);
    }

    private double SpeakerBubbleAnchorDistance(WorldAgentVisual visual)
    {
        var width = Math.Max(1, OverlayCanvas.ActualWidth);
        var height = Math.Max(1, OverlayCanvas.ActualHeight);
        var anchor = ProjectToOverlay(AgentWorldPoint(visual, 1.42), width, height, rejectOutsideViewport: false);
        if (anchor is null)
        {
            return double.MaxValue;
        }

        var clampedAnchor = ClampBubbleAnchor(anchor.Value, width, height);
        visual.Bubble.Measure(new Size(Math.Min(280, Math.Max(120, width - 32)), double.PositiveInfinity));
        var bubbleWidth = visual.Bubble.ActualWidth > 0 ? visual.Bubble.ActualWidth : visual.Bubble.DesiredSize.Width;
        var bubbleHeight = visual.Bubble.ActualHeight > 0 ? visual.Bubble.ActualHeight : visual.Bubble.DesiredSize.Height;
        var placement = CalculateBubblePlacement(new Size(bubbleWidth, bubbleHeight), clampedAnchor.X, clampedAnchor.Y, width, height);
        return placement.DistanceTo(clampedAnchor);
    }

    private double AttentionHaloAnchorDistance(WorldAgentVisual visual)
    {
        var width = Math.Max(1, OverlayCanvas.ActualWidth);
        var height = Math.Max(1, OverlayCanvas.ActualHeight);
        var anchor = ProjectToOverlay(AgentWorldPoint(visual, 0.18), width, height, rejectOutsideViewport: false);
        if (anchor is null)
        {
            return double.MaxValue;
        }

        visual.AttentionHalo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var haloWidth = visual.AttentionHalo.ActualWidth > 0 ? visual.AttentionHalo.ActualWidth : visual.AttentionHalo.DesiredSize.Width;
        var haloHeight = visual.AttentionHalo.ActualHeight > 0 ? visual.AttentionHalo.ActualHeight : visual.AttentionHalo.DesiredSize.Height;
        var center = new Point(
            ReadCanvasValue(Canvas.GetLeft(visual.AttentionHalo)) + (haloWidth / 2),
            ReadCanvasValue(Canvas.GetTop(visual.AttentionHalo)) + (haloHeight / 2));
        return Distance(anchor.Value, center);
    }


    private static double ReadCanvasValue(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
    }

    private GeometryModel3D CreateBox(Point3D center, double width, double height, double depth, Color color, double opacity = 1)
    {
        var material = Material(color, opacity);
        return new GeometryModel3D(CreateBoxMesh(width, height, depth), material)
        {
            BackMaterial = material,
            Transform = new TranslateTransform3D(center.X, center.Y, center.Z)
        };
    }

    private GeometryModel3D CreateJointBox(
        Point3D center,
        double width,
        double height,
        double depth,
        Color color,
        AxisAngleRotation3D lift,
        AxisAngleRotation3D spread,
        Point3D pivot,
        double opacity = 1)
    {
        var material = Material(color, opacity);
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
        transform.Children.Add(new RotateTransform3D(lift, pivot));
        transform.Children.Add(new RotateTransform3D(spread, pivot));
        return new GeometryModel3D(CreateBoxMesh(width, height, depth), material)
        {
            BackMaterial = material,
            Transform = transform
        };
    }

    private static MeshGeometry3D CreateBoxMesh(double width, double height, double depth)
    {
        var key = new BoxMeshKey(width, height, depth);
        lock (BoxMeshCacheLock)
        {
            if (BoxMeshCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var mesh = CreateBoxMeshCore(width, height, depth);
            BoxMeshCache[key] = mesh;
            return mesh;
        }
    }

    private static MeshGeometry3D CreateBoxMeshCore(double width, double height, double depth)
    {
        var x = width / 2;
        var y = height / 2;
        var z = depth / 2;
        var mesh = new MeshGeometry3D();

        AddFace(mesh, new Point3D(-x, -y, z), new Point3D(x, -y, z), new Point3D(x, y, z), new Point3D(-x, y, z), new Vector3D(0, 0, 1));
        AddFace(mesh, new Point3D(x, -y, -z), new Point3D(-x, -y, -z), new Point3D(-x, y, -z), new Point3D(x, y, -z), new Vector3D(0, 0, -1));
        AddFace(mesh, new Point3D(-x, -y, -z), new Point3D(-x, -y, z), new Point3D(-x, y, z), new Point3D(-x, y, -z), new Vector3D(-1, 0, 0));
        AddFace(mesh, new Point3D(x, -y, z), new Point3D(x, -y, -z), new Point3D(x, y, -z), new Point3D(x, y, z), new Vector3D(1, 0, 0));
        AddFace(mesh, new Point3D(-x, y, z), new Point3D(x, y, z), new Point3D(x, y, -z), new Point3D(-x, y, -z), new Vector3D(0, 1, 0));
        AddFace(mesh, new Point3D(-x, -y, -z), new Point3D(x, -y, -z), new Point3D(x, -y, z), new Point3D(-x, -y, z), new Vector3D(0, -1, 0));

        mesh.Freeze();
        return mesh;
    }

    private GeometryModel3D CreateSphere(Point3D center, double radius, Color color, double opacity = 1)
    {
        var material = Material(color, opacity);
        return new GeometryModel3D(CreateSphereMesh(radius), material)
        {
            BackMaterial = material,
            Transform = new TranslateTransform3D(center.X, center.Y, center.Z)
        };
    }

    private GeometryModel3D CreateCylinder(Point3D center, double radius, double height, Color color, double opacity = 1)
    {
        var material = Material(color, opacity);
        return new GeometryModel3D(CreateCylinderMesh(radius, height), material)
        {
            BackMaterial = material,
            Transform = new TranslateTransform3D(center.X, center.Y, center.Z)
        };
    }

    private static MeshGeometry3D CreateSphereMesh(double radius)
    {
        var key = new SphereMeshKey(radius);
        lock (RoundMeshCacheLock)
        {
            if (SphereMeshCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var mesh = CreateSphereMeshCore(radius);
            SphereMeshCache[key] = mesh;
            return mesh;
        }
    }

    private static MeshGeometry3D CreateSphereMeshCore(double radius)
    {
        const int stacks = 12;
        const int slices = 16;
        var mesh = new MeshGeometry3D();
        for (var stack = 0; stack <= stacks; stack++)
        {
            var phi = Math.PI * stack / stacks;
            var y = Math.Cos(phi);
            var ringRadius = Math.Sin(phi);
            for (var slice = 0; slice <= slices; slice++)
            {
                var theta = 2 * Math.PI * slice / slices;
                var normal = new Vector3D(ringRadius * Math.Cos(theta), y, ringRadius * Math.Sin(theta));
                mesh.Positions.Add(new Point3D(normal.X * radius, normal.Y * radius, normal.Z * radius));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point((double)slice / slices, (double)stack / stacks));
            }
        }

        var perRow = slices + 1;
        for (var stack = 0; stack < stacks; stack++)
        {
            for (var slice = 0; slice < slices; slice++)
            {
                var current = (stack * perRow) + slice;
                var next = current + perRow;
                mesh.TriangleIndices.Add(current);
                mesh.TriangleIndices.Add(next);
                mesh.TriangleIndices.Add(current + 1);
                mesh.TriangleIndices.Add(current + 1);
                mesh.TriangleIndices.Add(next);
                mesh.TriangleIndices.Add(next + 1);
            }
        }

        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D CreateCylinderMesh(double radius, double height)
    {
        var key = new CylinderMeshKey(radius, height);
        lock (RoundMeshCacheLock)
        {
            if (CylinderMeshCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var mesh = CreateCylinderMeshCore(radius, height);
            CylinderMeshCache[key] = mesh;
            return mesh;
        }
    }

    private static MeshGeometry3D CreateCylinderMeshCore(double radius, double height)
    {
        const int slices = 18;
        var mesh = new MeshGeometry3D();
        var half = height / 2;
        for (var slice = 0; slice <= slices; slice++)
        {
            var theta = 2 * Math.PI * slice / slices;
            var nx = Math.Cos(theta);
            var nz = Math.Sin(theta);
            var normal = new Vector3D(nx, 0, nz);
            mesh.Positions.Add(new Point3D(nx * radius, -half, nz * radius));
            mesh.Normals.Add(normal);
            mesh.TextureCoordinates.Add(new Point((double)slice / slices, 1));
            mesh.Positions.Add(new Point3D(nx * radius, half, nz * radius));
            mesh.Normals.Add(normal);
            mesh.TextureCoordinates.Add(new Point((double)slice / slices, 0));
        }

        for (var slice = 0; slice < slices; slice++)
        {
            var baseIndex = slice * 2;
            mesh.TriangleIndices.Add(baseIndex);
            mesh.TriangleIndices.Add(baseIndex + 1);
            mesh.TriangleIndices.Add(baseIndex + 2);
            mesh.TriangleIndices.Add(baseIndex + 2);
            mesh.TriangleIndices.Add(baseIndex + 1);
            mesh.TriangleIndices.Add(baseIndex + 3);
        }

        var topCenter = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(0, half, 0));
        mesh.Normals.Add(new Vector3D(0, 1, 0));
        mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        var bottomCenter = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(0, -half, 0));
        mesh.Normals.Add(new Vector3D(0, -1, 0));
        mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        for (var slice = 0; slice < slices; slice++)
        {
            var theta = 2 * Math.PI * slice / slices;
            var nextTheta = 2 * Math.PI * (slice + 1) / slices;
            var topA = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(Math.Cos(theta) * radius, half, Math.Sin(theta) * radius));
            mesh.Normals.Add(new Vector3D(0, 1, 0));
            mesh.TextureCoordinates.Add(new Point(0, 0));
            mesh.Positions.Add(new Point3D(Math.Cos(nextTheta) * radius, half, Math.Sin(nextTheta) * radius));
            mesh.Normals.Add(new Vector3D(0, 1, 0));
            mesh.TextureCoordinates.Add(new Point(1, 0));
            mesh.TriangleIndices.Add(topCenter);
            mesh.TriangleIndices.Add(topA);
            mesh.TriangleIndices.Add(topA + 1);
            var bottomA = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(Math.Cos(theta) * radius, -half, Math.Sin(theta) * radius));
            mesh.Normals.Add(new Vector3D(0, -1, 0));
            mesh.TextureCoordinates.Add(new Point(0, 1));
            mesh.Positions.Add(new Point3D(Math.Cos(nextTheta) * radius, -half, Math.Sin(nextTheta) * radius));
            mesh.Normals.Add(new Vector3D(0, -1, 0));
            mesh.TextureCoordinates.Add(new Point(1, 1));
            mesh.TriangleIndices.Add(bottomCenter);
            mesh.TriangleIndices.Add(bottomA + 1);
            mesh.TriangleIndices.Add(bottomA);
        }

        mesh.Freeze();
        return mesh;
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal)
    {
        var index = mesh.Positions.Count;
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Positions.Add(p3);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.TextureCoordinates.Add(new Point(0, 1));
        mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(1, 0));
        mesh.TextureCoordinates.Add(new Point(0, 0));
        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 1);
        mesh.TriangleIndices.Add(index + 2);
        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 2);
        mesh.TriangleIndices.Add(index + 3);
    }

    private static Material Material(Color color, double opacity)
    {
        var key = new MaterialKey(color, opacity);
        lock (MaterialCacheLock)
        {
            if (MaterialCache.TryGetValue(key, out var cached))
            {
                MaterialCacheUsage.Remove(cached.UsageNode);
                MaterialCacheUsage.AddLast(cached.UsageNode);
                return cached.Material;
            }

            var material = CreateMaterial(color, opacity);
            var usageNode = MaterialCacheUsage.AddLast(key);
            MaterialCache[key] = new MaterialCacheEntry(material, usageNode);
            while (MaterialCache.Count > MaterialCacheCapacity)
            {
                var oldest = MaterialCacheUsage.First;
                if (oldest is null)
                {
                    break;
                }

                MaterialCacheUsage.RemoveFirst();
                MaterialCache.Remove(oldest.Value);
            }

            return material;
        }
    }

    private static Material CreateMaterial(Color color, double opacity)
    {
        var diffuse = BrushFrom(color, opacity);
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(diffuse));
        group.Children.Add(new SpecularMaterial(BrushFrom(Color.FromRgb(235, 255, 248), Math.Min(0.38, opacity)), 32));
        group.Freeze();
        return group;
    }

    private static IEnumerable<GeometryModel3D> GeometryModels(Model3D model)
    {
        if (model is GeometryModel3D geometryModel)
        {
            yield return geometryModel;
            yield break;
        }

        if (model is not Model3DGroup group)
        {
            yield break;
        }

        foreach (var child in group.Children)
        {
            foreach (var geometry in GeometryModels(child))
            {
                yield return geometry;
            }
        }
    }

    private static bool MaterialIsFrozen(Material? material)
    {
        return material is null || material.IsFrozen;
    }

    private Color AccentColor(AgentWorldAvatar avatar)
    {
        var brush = AgentAccentService.ResolveBrush(
            avatar.Id,
            avatar.AccentColor,
            key => ResourceBrush(key, AccentResourceFallback(key)));
        if (brush is SolidColorBrush solid)
        {
            return solid.Color;
        }

        return AccentResourceFallback(AgentAccentService.NormalizeSpeakerId(avatar.Id) switch
        {
            "beta" => "BetaAccentBrush",
            "gamma" => "GammaAccentBrush",
            "delta" => "DeltaAccentBrush",
            "narrator" => "NarratorAccentBrush",
            "operator" => "OperatorAccentBrush",
            _ => "AlphaAccentBrush"
        });
    }

    private static Color AccentResourceFallback(string key)
    {
        return key switch
        {
            "BetaAccentBrush" => Color.FromRgb(240, 195, 106),
            "GammaAccentBrush" => Color.FromRgb(125, 217, 139),
            "DeltaAccentBrush" => Color.FromRgb(158, 166, 255),
            "NarratorAccentBrush" => Color.FromRgb(209, 133, 206),
            "OperatorAccentBrush" => Color.FromRgb(127, 183, 255),
            "MutedTextBrush" => Color.FromRgb(184, 199, 191),
            _ => Color.FromRgb(77, 212, 239)
        };
    }

    private Color ResourceColor(string key, Color fallback)
    {
        return ResourceBrush(key, fallback).Color;
    }

    private SolidColorBrush ResourceBrush(string key, Color fallback)
    {
        return TryFindResource(key) is SolidColorBrush brush
            ? brush
            : BrushFrom(fallback);
    }

    private static string DisplayStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status)
            ? "standing by"
            : status.Trim().ToLowerInvariant();
    }

    private static string NameTagStatus(AgentWorldAvatar avatar)
    {
        if (avatar.Speaking)
        {
            var speaking = new List<string> { "speaking" };
            if (avatar.Locked)
            {
                speaking.Add("locked");
            }

            if (avatar.BubbleTurn > 0)
            {
                speaking.Add($"turn {avatar.BubbleTurn}");
            }

            return string.Join(" | ", speaking);
        }

        var parts = new List<string>();
        if (avatar.HasError)
        {
            parts.Add("alert");
        }
        else if (avatar.Thinking)
        {
            parts.Add("thinking");
        }

        if (avatar.Locked)
        {
            parts.Add("locked");
        }

        if (avatar.HasToolActivity)
        {
            parts.Add("tool");
        }

        if (avatar.HasInternetActivity)
        {
            parts.Add("web");
        }

        if (parts.Count > 0)
        {
            return string.Join(" + ", parts);
        }

        var status = DisplayStatus(avatar.Status);
        return status.Equals("waiting", StringComparison.OrdinalIgnoreCase) ? "ready" : status;
    }

    private static string LegendDetail(AgentWorldAvatar avatar)
    {
        var parts = new List<string>();
        if (avatar.Speaking)
        {
            parts.Add(avatar.BubbleTurn > 0 ? $"speaking turn {avatar.BubbleTurn}" : "speaking");
        }
        else
        {
            parts.Add(NameTagStatus(avatar));
        }

        if (avatar.LastMessageTurn > 0 && !avatar.Speaking)
        {
            parts.Add($"last turn {avatar.LastMessageTurn}");
        }

        if (avatar.TotalTokens > 0)
        {
            parts.Add($"~{CompactWorldCount(avatar.TotalTokens)} tok");
        }

        if (avatar.HasToolActivity && !parts.Any(part => part.Contains("tool", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("tool");
        }

        if (avatar.HasInternetActivity && !parts.Any(part => part.Contains("web", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("web");
        }

        return string.Join(" | ", parts.Take(4));
    }

    private static string BubbleAutomationName(AgentWorldAvatar avatar)
    {
        return avatar.BubbleTurn > 0
            ? $"{avatar.Name} speech bubble, turn {avatar.BubbleTurn}"
            : $"{avatar.Name} speech bubble";
    }

    private static string TextContent(DependencyObject source)
    {
        if (source is TextBlock textBlock)
        {
            return textBlock.Text;
        }

        IEnumerable<DependencyObject> children = source switch
        {
            Panel panel => panel.Children.OfType<DependencyObject>(),
            Decorator { Child: { } child } => [child],
            ContentControl { Content: DependencyObject child } => [child],
            ContentControl { Content: string text } => [new TextBlock { Text = text }],
            _ => VisualChildren(source)
        };

        return string.Join(
            " ",
            children
                .Select(TextContent)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject source)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            yield return VisualTreeHelper.GetChild(source, index);
        }
    }

    private AgentWorldAvatar? SelectedAvatar()
    {
        return currentWorld?.Avatars.FirstOrDefault(avatar => avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase));
    }

    private AgentWorldAvatar? CurrentSpeakerAvatar()
    {
        return currentWorld?.Avatars
            .Where(avatar => avatar.Speaking)
            .OrderByDescending(avatar => avatar.BubbleTurn)
            .FirstOrDefault();
    }

    private static int IndexOfAvatar(IReadOnlyList<AgentWorldAvatar> avatars, string id)
    {
        for (var index = 0; index < avatars.Count; index++)
        {
            if (avatars[index].Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private WorldAgentVisual? SelectedVisual()
    {
        foreach (var visual in agentVisuals)
        {
            if (visual.Avatar.Id.Equals(selectedAgentId, StringComparison.OrdinalIgnoreCase))
            {
                return visual;
            }
        }

        return null;
    }

    private static string FallbackText(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string DisplayWorldValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        return value.Trim().Replace('_', ' ').Replace('-', ' ');
    }

    private static string MessageKindStatus(AgentWorldAvatar avatar)
    {
        var kind = string.IsNullOrWhiteSpace(avatar.LastMessageKind)
            ? "message"
            : avatar.LastMessageKind.Trim().Replace('_', ' ').Replace('-', ' ');
        var status = string.IsNullOrWhiteSpace(avatar.LastMessageStatus)
            ? "status unknown"
            : avatar.LastMessageStatus.Trim().Replace('_', ' ').Replace('-', ' ');
        return $"{kind} / {status}";
    }

    private static bool IsNarrator(AgentWorldAvatar avatar)
    {
        return avatar.Id.Equals("narrator", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EventLabels(AgentWorldAvatar avatar)
    {
        var labels = new List<string>();
        if (avatar.Speaking)
        {
            labels.Add("SPEAKING");
        }

        if (IsNarrator(avatar))
        {
            labels.Add("NARRATOR");
        }

        if (avatar.Locked)
        {
            labels.Add("LOCKED");
        }

        if (!DisplayWorldValue(avatar.VoiceStyle).Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            labels.Add("VOICE");
        }

        if (!DisplayWorldValue(avatar.PressureProfile).Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            labels.Add("PRESSURE");
        }

        if (avatar.Thinking)
        {
            labels.Add("THINKING");
        }

        if (avatar.HasToolActivity)
        {
            labels.Add("TOOL");
        }

        if (avatar.HasInternetActivity)
        {
            labels.Add("WEB");
        }

        if (avatar.HasError)
        {
            labels.Add("ALERT");
        }

        if (labels.Count == 0)
        {
            labels.Add("READY");
        }

        return labels;
    }

    private static bool IsInteractiveOverlay(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase ||
                source is TextBox ||
                source is ComboBox ||
                source is CheckBox)
            {
                return true;
            }

            if (source is FrameworkElement element &&
                (element.Tag is string ||
                 element.Tag is BubbleChrome ||
                 element.DataContext is string ||
                 element.Name == "WorldControlPanel" ||
                 element.Name == "WorldCuePanel" ||
                 element.Name == "AgentInspectorPanel" ||
                 element.Name == "WorldMiniMapPanel" ||
                 element.Name == "WorldLegendPanel"))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool ShouldIgnoreWorldWheel(DependencyObject? source)
    {
        return IsInteractiveOverlay(source);
    }

    private Rect ElementBounds(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || WorldRoot.ActualWidth <= 0 || WorldRoot.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        return element.TransformToAncestor(WorldRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private bool DebugWheelLeavesCameraDistance(UIElement source, int delta)
    {
        var before = cameraDistance;
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = MouseWheelEvent
        };
        source.RaiseEvent(args);
        return args.Handled && Math.Abs(cameraDistance - before) <= 0.001;
    }

    private static SolidColorBrush BrushFrom(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color)
        {
            Opacity = opacity
        };
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color first, Color second, double amount)
    {
        var clamped = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            BlendChannel(first.R, second.R, clamped),
            BlendChannel(first.G, second.G, clamped),
            BlendChannel(first.B, second.B, clamped));
    }

    private static byte BlendChannel(byte first, byte second, double amount)
    {
        return (byte)Math.Round(first + ((second - first) * amount));
    }

    private static double ToDegrees(double radians)
    {
        return radians * 180 / Math.PI;
    }

    private static double Distance(Point first, Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static int Mod(int value, int divisor)
    {
        return ((value % divisor) + divisor) % divisor;
    }

    private static double MaxAbs(params double[] values)
    {
        return values.Select(Math.Abs).DefaultIfEmpty(0).Max();
    }

    private double MinimumAgentSeparation()
    {
        if (agentVisuals.Count < 2)
        {
            return double.PositiveInfinity;
        }

        var minimum = double.PositiveInfinity;
        for (var firstIndex = 0; firstIndex < agentVisuals.Count; firstIndex++)
        {
            var first = new Point(agentVisuals[firstIndex].Translate.OffsetX, agentVisuals[firstIndex].Translate.OffsetZ);
            for (var secondIndex = firstIndex + 1; secondIndex < agentVisuals.Count; secondIndex++)
            {
                var second = new Point(agentVisuals[secondIndex].Translate.OffsetX, agentVisuals[secondIndex].Translate.OffsetZ);
                minimum = Math.Min(minimum, Distance(first, second));
            }
        }

        return minimum;
    }

    private static Point3D Lerp(Point3D first, Point3D second, double amount)
    {
        return new Point3D(
            first.X + ((second.X - first.X) * amount),
            first.Y + ((second.Y - first.Y) * amount),
            first.Z + ((second.Z - first.Z) * amount));
    }


    private readonly record struct BoxMeshKey(double Width, double Height, double Depth);

    private readonly record struct SphereMeshKey(double Radius);

    private readonly record struct CylinderMeshKey(double Radius, double Height);

    private readonly record struct MaterialKey(Color Color, double Opacity);

    private sealed record MaterialCacheEntry(Material Material, LinkedListNode<MaterialKey> UsageNode);

    private readonly record struct MiniMapMarkerRenderState(
        Color Accent,
        bool Selected,
        string Name,
        bool Speaking,
        string Status);

    internal readonly record struct AgentWorldRenderPolicy(
        bool RunContinuousAnimation,
        bool RenderStableFrame);

    private sealed record WorldAgentVisual(
        AgentWorldAvatar Avatar,
        Color Accent,
        string FollowBadgeLabel,
        Model3DGroup Model,
        Model3DGroup Shadow,
        ScaleTransform3D Scale,
        AxisAngleRotation3D Rotate,
        TranslateTransform3D Translate,
        ScaleTransform3D ShadowScale,
        TranslateTransform3D ShadowTranslate,
        AgentGestureRig Gesture,
        Grid AttentionHalo,
        ScaleTransform AttentionHaloScale,
        Border NameTag,
        Border Bubble,
        int LegPartCount,
        int ShadowPartCount,
        int SpotlightPartCount,
        int LockedBadgePartCount,
        int NarratorIdentityPartCount,
        int ActivityPropPartCount,
        int VoicePressurePartCount);

    internal readonly record struct BubblePlacement(
        double Left,
        double Top,
        double PointerX,
        double PointerY,
        bool PlacedBelow)
    {
        public double BodyTop => PlacedBelow ? Top + BubbleTailHeight : Top;

        public double DistanceTo(Point anchor)
        {
            return Distance(anchor, new Point(PointerX, PointerY));
        }
    }

    private sealed record BubbleChrome(
        Border Body,
        Polygon Pointer,
        TranslateTransform PointerTransform,
        Brush BodyBrush,
        Brush SelectedBodyBrush,
        Brush BorderBrush,
        Brush SelectedBorderBrush)
    {
        public bool? Selected { get; set; }

        public string AutomationStatus { get; set; } = "";
    }

    private sealed record AgentGestureRig(
        AxisAngleRotation3D BodyLean,
        AxisAngleRotation3D HeadNod,
        AxisAngleRotation3D LeftArmSpread,
        AxisAngleRotation3D RightArmSpread,
        AxisAngleRotation3D LeftArmLift,
        AxisAngleRotation3D RightArmLift,
        AxisAngleRotation3D LeftLegSwing,
        AxisAngleRotation3D RightLegSwing,
        AxisAngleRotation3D LeftKneeBend,
        AxisAngleRotation3D RightKneeBend,
        AxisAngleRotation3D LeftFootPitch,
        AxisAngleRotation3D RightFootPitch);

    private enum CameraDragMode
    {
        None,
        Orbit,
        Pan
    }

    private enum AgentWorldCameraMode
    {
        FollowSpeaker,
        Free,
        Overview
    }
}
