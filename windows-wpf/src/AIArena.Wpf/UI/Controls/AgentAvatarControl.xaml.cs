using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.Controls;

public partial class AgentAvatarControl : UserControl
{
    private const string SystemAvatarPath = "pack://application:,,,/Assets/Avatars/system_avatar.png";
    private static ImageSource? systemAvatarImage;
    private static bool systemAvatarLoadAttempted;

    public static readonly DependencyProperty AgentIdProperty = DependencyProperty.Register(
        nameof(AgentId),
        typeof(string),
        typeof(AgentAvatarControl),
        new PropertyMetadata("", OnAvatarChanged));

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName),
        typeof(string),
        typeof(AgentAvatarControl),
        new PropertyMetadata("", OnAvatarChanged));

    public static readonly DependencyProperty PersonaProperty = DependencyProperty.Register(
        nameof(Persona),
        typeof(string),
        typeof(AgentAvatarControl),
        new PropertyMetadata("", OnAvatarChanged));

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model),
        typeof(string),
        typeof(AgentAvatarControl),
        new PropertyMetadata("", OnAvatarChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(AgentAvatarControl),
        new PropertyMetadata(Brushes.DeepSkyBlue, OnAvatarChanged));

    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
        nameof(BaseBrush),
        typeof(Brush),
        typeof(AgentAvatarControl),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(13, 23, 32)), OnAvatarChanged));

    public static readonly DependencyProperty IsSystemProperty = DependencyProperty.Register(
        nameof(IsSystem),
        typeof(bool),
        typeof(AgentAvatarControl),
        new PropertyMetadata(false, OnAvatarChanged));

    public static readonly DependencyProperty UseChampionPortraitProperty = DependencyProperty.Register(
        nameof(UseChampionPortrait),
        typeof(bool),
        typeof(AgentAvatarControl),
        new PropertyMetadata(true, OnAvatarChanged));

    public static readonly DependencyProperty AvatarStyleProperty = DependencyProperty.Register(
        nameof(AvatarStyle),
        typeof(string),
        typeof(AgentAvatarControl),
        new PropertyMetadata("pack", OnAvatarChanged));

    public static readonly DependencyProperty UseSystemGlyphProperty = DependencyProperty.Register(
        nameof(UseSystemGlyph),
        typeof(bool),
        typeof(AgentAvatarControl),
        new PropertyMetadata(true, OnAvatarChanged));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText),
        typeof(string),
        typeof(AgentAvatarControl),
        new PropertyMetadata("?", OnAvatarChanged));

    public AgentAvatarControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Redraw();
    }

    public string AgentId
    {
        get => (string)GetValue(AgentIdProperty);
        set => SetValue(AgentIdProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string Persona
    {
        get => (string)GetValue(PersonaProperty);
        set => SetValue(PersonaProperty, value);
    }

    public string Model
    {
        get => (string)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush BaseBrush
    {
        get => (Brush)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public bool IsSystem
    {
        get => (bool)GetValue(IsSystemProperty);
        set => SetValue(IsSystemProperty, value);
    }

    public bool UseChampionPortrait
    {
        get => (bool)GetValue(UseChampionPortraitProperty);
        set => SetValue(UseChampionPortraitProperty, value);
    }

    public string AvatarStyle
    {
        get => (string)GetValue(AvatarStyleProperty);
        set => SetValue(AvatarStyleProperty, value);
    }

    public bool UseSystemGlyph
    {
        get => (bool)GetValue(UseSystemGlyphProperty);
        set => SetValue(UseSystemGlyphProperty, value);
    }

    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    private static void OnAvatarChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is AgentAvatarControl control && control.IsLoaded)
        {
            control.Redraw();
        }
    }

    private void Redraw()
    {
        try
        {
            AvatarCanvas.Children.Clear();
            DrawOuterRing();

            if (IsSystem)
            {
                if (UseSystemGlyph)
                {
                    if (!DrawSystemAvatar())
                    {
                        DrawSystemGlyph();
                    }
                }
                else
                {
                    DrawFallback();
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(DisplayName) && string.IsNullOrWhiteSpace(AgentId))
            {
                DrawFallback();
                return;
            }

            var seed = StableHash($"{AgentId}|{DisplayName}|{Persona}|{Model}");
            switch (NormalizeAvatarStyle())
            {
                case "initials":
                    DrawFallback();
                    break;
                case "simple":
                    DrawSimpleRobot(seed);
                    break;
                case "procedural":
                    DrawChampionRobot(seed);
                    break;
                default:
                    if (!DrawSpriteAvatar())
                    {
                        DrawChampionRobot(seed);
                    }

                    break;
            }
        }
        catch
        {
            AvatarCanvas.Children.Clear();
            DrawOuterRing();
            DrawFallback();
        }
    }

    private void DrawOuterRing()
    {
        AddShape(new Ellipse
        {
            Width = 42,
            Height = 42,
            Fill = FillBrush(0.14),
            Stroke = BrightAccent(0.22),
            StrokeThickness = 1.7
        }, 1, 1);
    }

    private bool DrawSpriteAvatar()
    {
        var imageSource = AvatarSpriteSheetService.GetAvatarForSpeaker(AgentId, DisplayName, Persona, Model);
        if (imageSource is null)
        {
            return false;
        }

        var image = new Image
        {
            Source = imageSource,
            Width = 37,
            Height = 37,
            Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = true,
            Clip = new EllipseGeometry(new Point(18.5, 18.5), 18.5, 18.5)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, 3.5);
        Canvas.SetTop(image, 3.5);
        AvatarCanvas.Children.Add(image);
        return true;
    }

    private bool DrawSystemAvatar()
    {
        var imageSource = LoadSystemAvatar();
        if (imageSource is null)
        {
            return false;
        }

        var image = new Image
        {
            Source = imageSource,
            Width = 37,
            Height = 37,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Clip = new EllipseGeometry(new Point(18.5, 18.5), 18.5, 18.5)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, 3.5);
        Canvas.SetTop(image, 3.5);
        AvatarCanvas.Children.Add(image);
        return true;
    }

    private static ImageSource? LoadSystemAvatar()
    {
        if (systemAvatarLoadAttempted)
        {
            return systemAvatarImage;
        }

        systemAvatarLoadAttempted = true;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(SystemAvatarPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            systemAvatarImage = bitmap;
        }
        catch
        {
            systemAvatarImage = null;
        }

        return systemAvatarImage;
    }

    private void DrawChampionRobot(uint seed)
    {
        var role = NormalizeRole();
        var variant = RoleVariant(role, seed);
        DrawChampionAntenna(role, (int)((seed >> 9) % 5));
        DrawChampionHead(variant);
        DrawChampionPanels(role, seed);
        DrawChampionEyes(role, (int)((seed >> 3) % 6));
        DrawChampionMouth(role, (int)((seed >> 13) % 5));
        DrawChampionMarks(role, seed);
    }

    private void DrawChampionHead(int variant)
    {
        var shell = MetalBrush(0.18);
        var plate = MetalBrush(0.34);
        var stroke = BrightAccent(0.15);

        switch (variant)
        {
            case 0:
                AddShape(new Path
                {
                    Data = Geometry.Parse("M9,36 L9,17 C10,7 34,7 35,17 L35,36 Z"),
                    Fill = shell,
                    Stroke = stroke,
                    StrokeThickness = 1.5
                }, 0, 0);
                break;
            case 1:
                AvatarCanvas.Children.Add(new Polygon
                {
                    Points = Points(new Point(10, 11), new Point(34, 11), new Point(38, 18), new Point(35, 36), new Point(9, 36), new Point(6, 18)),
                    Fill = shell,
                    Stroke = stroke,
                    StrokeThickness = 1.5
                });
                break;
            case 2:
                AvatarCanvas.Children.Add(new Polygon
                {
                    Points = Points(new Point(14, 8), new Point(30, 8), new Point(38, 18), new Point(32, 37), new Point(12, 37), new Point(6, 18)),
                    Fill = shell,
                    Stroke = stroke,
                    StrokeThickness = 1.5
                });
                break;
            case 3:
                AddShape(new Rectangle
                {
                    Width = 28,
                    Height = 27,
                    RadiusX = 8,
                    RadiusY = 8,
                    Fill = shell,
                    Stroke = stroke,
                    StrokeThickness = 1.5
                }, 8, 10);
                break;
            case 4:
                AvatarCanvas.Children.Add(new Polygon
                {
                    Points = Points(new Point(9, 14), new Point(35, 14), new Point(33, 31), new Point(27, 38), new Point(17, 38), new Point(11, 31)),
                    Fill = shell,
                    Stroke = stroke,
                    StrokeThickness = 1.5
                });
                break;
            default:
                AvatarCanvas.Children.Add(new Polygon
                {
                    Points = Points(new Point(8, 15), new Point(15, 9), new Point(29, 9), new Point(36, 15), new Point(34, 36), new Point(10, 36)),
                    Fill = shell,
                    Stroke = stroke,
                    StrokeThickness = 1.5
                });
                break;
        }

        AddShape(new Path
        {
            Data = Geometry.Parse("M13,15 L22,12 L31,15 L30,19 L14,19 Z"),
            Fill = plate,
            Stroke = Blend(plate, BrightAccent(0.3), 0.25),
            StrokeThickness = 1
        }, 0, 0);
    }

    private void DrawChampionPanels(string role, uint seed)
    {
        var plate = MetalBrush(0.44);
        var accent = BrightAccent(0.12);

        if (role == "beta")
        {
            AddShape(new Rectangle { Width = 6, Height = 14, RadiusX = 2, RadiusY = 2, Fill = plate, Stroke = accent, StrokeThickness = 1 }, 8, 20);
            AddShape(new Rectangle { Width = 6, Height = 14, RadiusX = 2, RadiusY = 2, Fill = plate, Stroke = accent, StrokeThickness = 1 }, 30, 20);
        }
        else if (role == "gamma")
        {
            AddShape(new Ellipse { Width = 8, Height = 8, Fill = FillBrush(0.2), Stroke = accent, StrokeThickness = 1.4 }, 8.5, 22);
            AddShape(new Ellipse { Width = 8, Height = 8, Fill = FillBrush(0.2), Stroke = accent, StrokeThickness = 1.4 }, 27.5, 22);
        }
        else if (role == "delta")
        {
            AddShape(new Path { Data = Geometry.Parse("M9,24 L16,17 L17,33 L10,32 Z"), Fill = plate, Stroke = accent, StrokeThickness = 0.8 }, 0, 0);
            AddShape(new Path { Data = Geometry.Parse("M35,24 L28,17 L27,33 L34,32 Z"), Fill = plate, Stroke = accent, StrokeThickness = 0.8 }, 0, 0);
        }
        else if (role == "narrator")
        {
            AddShape(new Path { Data = Geometry.Parse("M10,23 L17,18 L17,33 L10,31 Z"), Fill = plate }, 0, 0);
            AddShape(new Path { Data = Geometry.Parse("M34,23 L27,18 L27,33 L34,31 Z"), Fill = plate }, 0, 0);
        }
        else
        {
            AddShape(new Path { Data = Geometry.Parse("M10,21 L15,18 L15,33 L10,31 Z"), Fill = plate }, 0, 0);
            AddShape(new Path { Data = Geometry.Parse("M34,21 L29,18 L29,33 L34,31 Z"), Fill = plate }, 0, 0);
        }

        if (((seed >> 18) & 1) == 0)
        {
            AddShape(new Rectangle { Width = 4, Height = 1.8, RadiusX = 0.9, RadiusY = 0.9, Fill = BrightAccent(0.32) }, 20, 14);
        }
    }

    private void DrawChampionEyes(string role, int variant)
    {
        var glow = BrightAccent(0.45);
        var darkGlass = Blend(BaseBrush, Brushes.Black, 0.58);

        if (role == "narrator")
        {
            AddShape(new Ellipse { Width = 13, Height = 13, Fill = darkGlass, Stroke = glow, StrokeThickness = 2 }, 15.5, 18);
            AddShape(new Ellipse { Width = 5, Height = 5, Fill = glow }, 19.5, 22);
            return;
        }

        if (role == "delta")
        {
            AddShape(new Rectangle { Width = 20, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = darkGlass, Stroke = glow, StrokeThickness = 1.5 }, 12, 20);
            AddShape(new Rectangle { Width = 5, Height = 2.5, RadiusX = 1.2, RadiusY = 1.2, Fill = glow }, 15, 21.2);
            AddShape(new Rectangle { Width = 5, Height = 2.5, RadiusX = 1.2, RadiusY = 1.2, Fill = glow }, 24, 21.2);
            return;
        }

        switch (variant)
        {
            case 0:
                AddShape(new Rectangle { Width = 18, Height = 6, RadiusX = 3, RadiusY = 3, Fill = darkGlass, Stroke = glow, StrokeThickness = 1.5 }, 13, 20);
                AddShape(new Rectangle { Width = 12, Height = 2, RadiusX = 1, RadiusY = 1, Fill = glow }, 16, 22);
                break;
            case 1:
                AddShape(new Ellipse { Width = 6, Height = 6, Fill = glow }, 14, 20);
                AddShape(new Ellipse { Width = 6, Height = 6, Fill = glow }, 24, 20);
                break;
            case 2:
                AvatarCanvas.Children.Add(new Polygon { Points = Points(new Point(12, 20), new Point(20, 21), new Point(18, 25), new Point(13, 24)), Fill = glow });
                AvatarCanvas.Children.Add(new Polygon { Points = Points(new Point(32, 20), new Point(24, 21), new Point(26, 25), new Point(31, 24)), Fill = glow });
                break;
            case 3:
                AddShape(new Ellipse { Width = 10, Height = 10, Fill = darkGlass, Stroke = glow, StrokeThickness = 1.7 }, 17, 18);
                AddShape(new Ellipse { Width = 3.5, Height = 3.5, Fill = glow }, 20.2, 21.3);
                break;
            case 4:
                AddShape(new Rectangle { Width = 4, Height = 12, RadiusX = 2, RadiusY = 2, Fill = glow }, 20, 17);
                break;
            default:
                AddShape(new Ellipse { Width = 7, Height = 7, Fill = glow }, 12.5, 19);
                AddShape(new Rectangle { Width = 9, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = glow }, 23, 20);
                break;
        }
    }

    private void DrawChampionMouth(string role, int variant)
    {
        var mouth = Blend(AccentBrush, Brushes.White, 0.12);

        if (role == "gamma" && variant % 2 == 0)
        {
            AddShape(new Rectangle { Width = 13, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = FillBrush(0.22), Stroke = mouth, StrokeThickness = 1 }, 15.5, 31);
            return;
        }

        switch (variant)
        {
            case 0:
                AddShape(new Line { X1 = 16, Y1 = 31, X2 = 28, Y2 = 31, Stroke = mouth, StrokeThickness = 1.5 }, 0, 0);
                break;
            case 1:
                for (var i = 0; i < 4; i++)
                {
                    AddShape(new Line { X1 = 16 + (i * 3), Y1 = 29, X2 = 16 + (i * 3), Y2 = 34, Stroke = mouth, StrokeThickness = 1.4 }, 0, 0);
                }
                break;
            case 2:
                AddShape(new Rectangle { Width = 3, Height = 3, Fill = mouth }, 16, 31);
                AddShape(new Rectangle { Width = 3, Height = 3, Fill = mouth }, 21, 31);
                AddShape(new Rectangle { Width = 3, Height = 3, Fill = mouth }, 26, 31);
                break;
            case 3:
                AvatarCanvas.Children.Add(new Polygon { Points = Points(new Point(15, 30), new Point(29, 30), new Point(26, 35), new Point(18, 35)), Fill = FillBrush(0.22), Stroke = mouth, StrokeThickness = 1 });
                break;
        }
    }

    private void DrawChampionAntenna(string role, int variant)
    {
        var glow = BrightAccent(0.35);
        if (role == "alpha")
        {
            AddShape(new Line { X1 = 22, Y1 = 10, X2 = 22, Y2 = 4, Stroke = glow, StrokeThickness = 1.6 }, 0, 0);
            AddShape(new Ellipse { Width = 4, Height = 4, Fill = glow }, 20, 2);
            return;
        }

        if (role == "beta" || variant == 2)
        {
            AddShape(new Rectangle { Width = 5, Height = 9, RadiusX = 1.5, RadiusY = 1.5, Fill = glow }, 5, 19);
            AddShape(new Rectangle { Width = 5, Height = 9, RadiusX = 1.5, RadiusY = 1.5, Fill = glow }, 34, 19);
            return;
        }

        if (variant == 1)
        {
            AddShape(new Line { X1 = 16, Y1 = 11, X2 = 13, Y2 = 6, Stroke = glow, StrokeThickness = 1.4 }, 0, 0);
            AddShape(new Line { X1 = 28, Y1 = 11, X2 = 31, Y2 = 6, Stroke = glow, StrokeThickness = 1.4 }, 0, 0);
        }
    }

    private void DrawChampionMarks(string role, uint seed)
    {
        var light = BrightAccent(0.5);
        if (role == "operator")
        {
            AddShape(new Rectangle { Width = 14, Height = 2.4, RadiusX = 1.2, RadiusY = 1.2, Fill = light }, 15, 11);
        }
        else if (role == "delta")
        {
            AddShape(new Line { X1 = 12, Y1 = 35, X2 = 32, Y2 = 35, Stroke = light, StrokeThickness = 1.5 }, 0, 0);
            AddShape(new Line { X1 = 18, Y1 = 11, X2 = 26, Y2 = 11, Stroke = light, StrokeThickness = 1.4 }, 0, 0);
        }
        else if (role == "alpha")
        {
            AddShape(new Line { X1 = 13, Y1 = 27, X2 = 17, Y2 = 29, Stroke = light, StrokeThickness = 1.6 }, 0, 0);
            AddShape(new Line { X1 = 31, Y1 = 27, X2 = 27, Y2 = 29, Stroke = light, StrokeThickness = 1.6 }, 0, 0);
        }
        else if (role == "gamma")
        {
            AddShape(new Ellipse { Width = 3, Height = 3, Fill = light }, 12, 30);
            AddShape(new Ellipse { Width = 3, Height = 3, Fill = light }, 29, 30);
        }
        else if (((seed >> 21) & 1) == 0)
        {
            AddShape(new Rectangle { Width = 5, Height = 2, RadiusX = 1, RadiusY = 1, Fill = light }, 19.5, 11.5);
        }
    }

    private void DrawSimpleRobot(uint seed)
    {
        var headVariant = (int)(seed % 4);
        var eyeVariant = (int)((seed >> 3) % 4);
        DrawSimpleAntenna((int)((seed >> 7) % 4));
        DrawSimpleHead(headVariant);
        DrawSimpleEyes(eyeVariant);
        DrawSimpleMouth((int)((seed >> 11) % 4));
    }

    private void DrawSimpleHead(int variant)
    {
        var fill = FillBrush(0.32);
        var stroke = Blend(AccentBrush, Brushes.White, 0.25);
        switch (variant)
        {
            case 0:
                AddShape(new Rectangle { Width = 24, Height = 22, RadiusX = 6, RadiusY = 6, Fill = fill, Stroke = stroke, StrokeThickness = 1.4 }, 10, 12);
                break;
            case 1:
                AddShape(new Rectangle { Width = 24, Height = 22, RadiusX = 2, RadiusY = 2, Fill = fill, Stroke = stroke, StrokeThickness = 1.4 }, 10, 12);
                break;
            case 2:
                AddShape(new Path { Data = Geometry.Parse("M10,34 L10,22 C10,12 34,12 34,22 L34,34 Z"), Fill = fill, Stroke = stroke, StrokeThickness = 1.4 }, 0, 0);
                break;
            default:
                AvatarCanvas.Children.Add(new Polygon
                {
                    Points = Points(new Point(16, 12), new Point(28, 12), new Point(36, 20), new Point(32, 34), new Point(12, 34), new Point(8, 20)),
                    Fill = fill,
                    Stroke = stroke,
                    StrokeThickness = 1.4
                });
                break;
        }
    }

    private void DrawSimpleEyes(int variant)
    {
        var accent = AccentBrush;
        switch (variant)
        {
            case 0:
                AddShape(new Ellipse { Width = 4.2, Height = 4.2, Fill = accent }, 15, 20);
                AddShape(new Ellipse { Width = 4.2, Height = 4.2, Fill = accent }, 25, 20);
                break;
            case 1:
                AddShape(new Rectangle { Width = 14, Height = 4, RadiusX = 2, RadiusY = 2, Fill = accent }, 15, 20);
                break;
            case 2:
                AddShape(new Ellipse { Width = 8, Height = 8, Fill = FillBrush(0.15), Stroke = accent, StrokeThickness = 1.5 }, 18, 18.5);
                break;
            default:
                AddShape(new Rectangle { Width = 16, Height = 2.4, RadiusX = 1.2, RadiusY = 1.2, Fill = accent }, 14, 21);
                break;
        }
    }

    private void DrawSimpleMouth(int variant)
    {
        var stroke = Blend(AccentBrush, Brushes.White, 0.1);
        switch (variant)
        {
            case 1:
                AddShape(new Line { X1 = 16, Y1 = 29, X2 = 28, Y2 = 29, Stroke = stroke, StrokeThickness = 1.3 }, 0, 0);
                break;
            case 2:
                for (var i = 0; i < 3; i++)
                {
                    AddShape(new Line { X1 = 17 + (i * 4), Y1 = 28, X2 = 17 + (i * 4), Y2 = 31, Stroke = stroke, StrokeThickness = 1.2 }, 0, 0);
                }

                break;
            case 3:
                AddShape(new Rectangle { Width = 2.4, Height = 2.4, Fill = stroke }, 16, 29);
                AddShape(new Rectangle { Width = 2.4, Height = 2.4, Fill = stroke }, 21, 29);
                AddShape(new Rectangle { Width = 2.4, Height = 2.4, Fill = stroke }, 26, 29);
                break;
        }
    }

    private void DrawSimpleAntenna(int variant)
    {
        var accent = Blend(AccentBrush, Brushes.White, 0.18);
        switch (variant)
        {
            case 1:
                AddShape(new Line { X1 = 22, Y1 = 12, X2 = 22, Y2 = 7, Stroke = accent, StrokeThickness = 1.2 }, 0, 0);
                AddShape(new Ellipse { Width = 3.4, Height = 3.4, Fill = accent }, 20.3, 5);
                break;
            case 2:
                AddShape(new Line { X1 = 17, Y1 = 13, X2 = 14, Y2 = 8, Stroke = accent, StrokeThickness = 1.2 }, 0, 0);
                AddShape(new Line { X1 = 27, Y1 = 13, X2 = 30, Y2 = 8, Stroke = accent, StrokeThickness = 1.2 }, 0, 0);
                break;
            case 3:
                AddShape(new Rectangle { Width = 3.5, Height = 7, RadiusX = 1, RadiusY = 1, Fill = accent }, 7, 20);
                AddShape(new Rectangle { Width = 3.5, Height = 7, RadiusX = 1, RadiusY = 1, Fill = accent }, 33.5, 20);
                break;
        }
    }

    private void DrawSystemGlyph()
    {
        var stroke = Blend(AccentBrush, Brushes.White, 0.12);
        var fill = FillBrush(0.3);
        AddShape(new Rectangle { Width = 24, Height = 17, RadiusX = 3, RadiusY = 3, Fill = fill, Stroke = stroke, StrokeThickness = 1.5 }, 10, 13);
        AddShape(new Rectangle { Width = 18, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = stroke }, 13, 17);
        AddShape(new Ellipse { Width = 3, Height = 3, Fill = stroke }, 14, 24);
        AddShape(new Ellipse { Width = 3, Height = 3, Fill = stroke }, 20.5, 24);
        AddShape(new Ellipse { Width = 3, Height = 3, Fill = stroke }, 27, 24);
        AvatarCanvas.Children.Add(new Polygon
        {
            Points = Points(new Point(22, 7), new Point(38, 36), new Point(6, 36)),
            Fill = Brushes.Transparent,
            Stroke = stroke,
            StrokeThickness = 1.4
        });
    }

    private void DrawFallback()
    {
        AvatarCanvas.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(FallbackText) ? "?" : FallbackText[..1].ToUpperInvariant(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Width = 44,
            Height = 44,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(0, 12, 0, 0)
        });
    }

    private int RoleVariant(string role, uint seed)
    {
        return role switch
        {
            "alpha" => 2,
            "beta" => 5,
            "gamma" => 3,
            "delta" => 0,
            "narrator" => 4,
            "operator" => 1,
            _ => (int)(seed % 6)
        };
    }

    private string NormalizeRole()
    {
        var role = string.IsNullOrWhiteSpace(AgentId) ? DisplayName : AgentId;
        role = role.Trim().ToLowerInvariant();
        if (role.Contains("alpha", StringComparison.OrdinalIgnoreCase))
        {
            return "alpha";
        }

        if (role.Contains("beta", StringComparison.OrdinalIgnoreCase))
        {
            return "beta";
        }

        if (role.Contains("gamma", StringComparison.OrdinalIgnoreCase))
        {
            return "gamma";
        }

        if (role.Contains("delta", StringComparison.OrdinalIgnoreCase))
        {
            return "delta";
        }

        if (role.Contains("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return "narrator";
        }

        if (role.Contains("operator", StringComparison.OrdinalIgnoreCase))
        {
            return "operator";
        }

        return role;
    }

    private string NormalizeAvatarStyle()
    {
        if (string.IsNullOrWhiteSpace(AvatarStyle))
        {
            return UseChampionPortrait ? "procedural" : "simple";
        }

        return AvatarStyle.Trim().ToLowerInvariant();
    }

    private Brush FillBrush(double accentAmount)
    {
        return Blend(BaseBrush, AccentBrush, accentAmount);
    }

    private Brush MetalBrush(double lightAmount)
    {
        return Blend(FillBrush(0.18), Brushes.White, lightAmount);
    }

    private Brush BrightAccent(double whiteAmount)
    {
        return Blend(AccentBrush, Brushes.White, whiteAmount);
    }

    private void AddShape(Shape shape, double left, double top)
    {
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        AvatarCanvas.Children.Add(shape);
    }

    private static SolidColorBrush Blend(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        var baseColor = ColorOf(baseBrush, Color.FromRgb(13, 23, 32));
        var accentColor = ColorOf(accentBrush, Colors.DeepSkyBlue);
        var amount = Math.Clamp(accentAmount, 0, 1);
        return new SolidColorBrush(Color.FromRgb(
            BlendChannel(baseColor.R, accentColor.R, amount),
            BlendChannel(baseColor.G, accentColor.G, amount),
            BlendChannel(baseColor.B, accentColor.B, amount)));
    }

    private static Color ColorOf(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private static byte BlendChannel(byte baseline, byte accent, double amount)
    {
        return (byte)Math.Round(baseline + ((accent - baseline) * amount));
    }

    private static PointCollection Points(params Point[] points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }

        return collection;
    }

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var item in Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()))
        {
            hash ^= item;
            hash *= prime;
        }

        return hash;
    }
}
