using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

using SkiaSharp;

using Demo3D.AspectLibrary;
using Demo3D.Common;
using Demo3D.Components.Properties;
using Demo3D.Gui;
using Demo3D.Gui.AspectViewer;
using Demo3D.Plugins;
using Demo3D.Visuals;
using Demo3D.Licensing;
using Demo3D.Wpf;

namespace Demo3D.Components {
    [License("CITM", typeof(Layout3DProfessionalEditionPlugin), typeof(Demo3DRuntimeEditionPlugin), typeof(Sim3DRuntimeEditionPlugin))]
    public class CAD_Is_The_Model : Plugin
    {
        private const int GlyphSize = 32;
        private const int LargeGlyphSize = 64;

        private Library aspectLibrary;

        private string AspectLibraryPath
        {
            get { return app.Options.AspectLibraryOptions.GetAspectLibraryPath(app); }
        }

        public CAD_Is_The_Model()
        {
            // Nothing to do.
        }

        public override void Install(IBuilder app)
        {
            base.Install(app);

            if (app.GetPlugin(AspectLibraryPlugin.LicensedFeature) is AspectLibraryPlugin aspectLibraryPlugin)
            {
                aspectLibraryPlugin.AfterAspectLibraryUpdated += OnAfterAspectLibraryUpdated;
            }

            this.LoadToolbar();
        }

        public override void Uninstall()
        {
            this.UnloadToolbar();

            if (app.GetPlugin(AspectLibraryPlugin.LicensedFeature) is AspectLibraryPlugin aspectLibraryPlugin)
            {
                aspectLibraryPlugin.AfterAspectLibraryUpdated -= OnAfterAspectLibraryUpdated;
            }
        }

        private void OnAfterAspectLibraryUpdated(object sender, EventArgs e)
        {
            this.LoadToolbar();
        }

        private void LoadAspectLibraryFile()
        {
            try
            {
                this.aspectLibrary = LibrarySerializer.LoadFromFile(this.AspectLibraryPath);
            }
            catch
            {
                if (this.aspectLibrary != null)
                {
                    this.aspectLibrary.Clear();
                    this.aspectLibrary = null;
                }
            }
        }

        private void UnloadToolbar()
        {
            var ribbonManager = app.CustomRibbonManager;
            var ribbonPage = ribbonManager.FindPage("CAD Is The Model") as IRibbonPage;
            if (ribbonPage != null)
            {
                ribbonPage.Clear();
                ribbonPage.Destroy();
            }
        }

        private void LoadToolbar()
        {
            this.UnloadToolbar();

            var aspectsPath = Path.GetDirectoryName(this.AspectLibraryPath);

            var ribbonManager = this.app.CustomRibbonManager;
            var ribbonPage = ribbonManager.FindCreatePage("CAD Is The Model") as IRibbonPage;
            var ribbonGroup = ribbonPage.FindCreateGroup("Aspects") as IRibbonGroup;

            this.LoadAspectLibraryFile();
            if (this.aspectLibrary != null)
            {
                var core = Assembly.Load("Demo3D.Core");
                var types = GetExportableTypes(core).ToDictionary(t => t.FullName);

                var allResources = this.app.Document.Resources.AsEnumerable()
                    .Concat(this.app.CatalogManager.Catalogs.Select(c => c.Resources)).ToArray();

                foreach (var resources in allResources)
                {
                    var assemblies = resources.Scripts.SelectNotNull(s => s.NativeAssembly).ToList();
                    foreach (var assembly in assemblies)
                    {
                        foreach (var type in GetExportableTypes(assembly))
                        {
                            if (types.ContainsKey(type.FullName) == false)
                            {
                                types[type.FullName] = type;
                            }
                        }
                    }
                }

                BitmapSource unknownGlyph = null;
                BitmapSource unknownLargeGlyph = null;

                foreach (var aspect in this.aspectLibrary.Classes)
                {
                    if (aspect == null) { continue; }
                    if (types.TryGetValue(aspect.FullName, out var type))
                    {
                        var obsolete = type.GetCustomAttribute<ObsoleteAttribute>();
                        if (obsolete == null)
                        {
                            var aspectDisplayName = GetAspectDisplayName(aspect);
                            var categoryDisplayName = GetAspectCategoryDisplayName(aspect);
                            var category = ribbonGroup.FindCreateSub(categoryDisplayName);

                            var button = category.FindCreateButton(aspectDisplayName);
                            button.Click += (sender, e) => this.CreateAspect(this.app.Selection, type);

                            var help = type.GetCustomAttribute<HelpUrlAttribute>();
                            if (help != null)
                            {
                                button.HelpPage = help.Url;
                            }

                            var glyph = LoadAndCacheGlyph(type, new Size(GlyphSize, GlyphSize));
                            var largeGlyph = LoadAndCacheGlyph(type, new Size(LargeGlyphSize, LargeGlyphSize));

                            if (glyph != null)
                            {
                                button.Glyph = glyph;
                            }
                            else
                            {
                                if (unknownGlyph == null)
                                {
                                    unknownGlyph = LoadSVGGlyph(Resources.UnknownAspect, new Size(GlyphSize, GlyphSize));
                                }

                                button.Glyph = unknownGlyph;
                            }

                            if (largeGlyph != null)
                            {
                                button.LargeGlyph = largeGlyph;
                            }
                            else
                            {
                                if (unknownLargeGlyph == null)
                                {
                                    unknownLargeGlyph = LoadSVGGlyph(Resources.UnknownAspect, new Size(LargeGlyphSize, LargeGlyphSize));
                                }

                                button.LargeGlyph = unknownLargeGlyph;
                            }

                            if (category.Glyph == null)
                            {
                                category.Glyph = button.Glyph;
                                category.LargeGlyph = button.LargeGlyph;
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<Type> GetExportableTypes(Assembly assembly)
        {
            return assembly.GetExportedTypes().Where(t => typeof(ExportableVisualAspect).IsAssignableFrom(t) && t.IsClass == true && t.IsAbstract == false);
        }

        private static string CachedIconPath(string iconName, Size size)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                iconName.Replace(c, '_');
            }

            var iconCacheDirectory = Path.Combine(Path.GetTempPath(), "emulate3d-icons");
            Directory.CreateDirectory(iconCacheDirectory);

            return Path.Combine(iconCacheDirectory, $"{iconName}_{size.Width}_{size.Height}.png");
        }

        private static BitmapSource LoadAndCacheGlyph(Type type, Size size)
        {
            var typeName = type.Name.Replace("Aspect", ""); // Strip Aspect from the name

            var glyphCachedPath = CachedIconPath(typeName, size);

            if (File.Exists(glyphCachedPath))
            {
                return LoadPNGGlyph(File.ReadAllBytes(glyphCachedPath));
            }
            else
            {
                foreach (var resourceName in type.Assembly.GetManifestResourceNames())
                {
                    var paths = resourceName.Split('.');
                    if (paths?.Length < 2) { continue; }

                    var extension = paths[paths.Length - 1].ToLowerInvariant();
                    if (extension != "png" && extension != "svg") { continue; }

                    var iconName = paths[paths.Length - 2];

                    if (string.Equals(typeName, iconName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        byte[] iconBytes;
                        using (var iconStream = type.Assembly.GetManifestResourceStream(resourceName))
                        {
                            using (var ms = new MemoryStream())
                            {
                                iconStream.CopyTo(ms);
                                iconBytes = ms.ToArray();
                            }
                        }

                        BitmapSource glyph = null;
                        switch (extension)
                        {
                            case "svg":
                                glyph = LoadSVGGlyph(iconBytes, size);
                                break;

                            case "png":
                                glyph = LoadPNGGlyph(iconBytes);
                                break;
                        }

                        if (glyph != null)
                        {
                            using (var fs = new FileStream(glyphCachedPath, FileMode.Create))
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(glyph));
                                encoder.Save(fs);
                            }
                        }

                        return glyph;
                    }
                }
            }

            return null;
        }

        private static BitmapSource LoadGlyph(string path, Size targetSize)
        {
            var bitmap = ConvertSVG(path, targetSize);
            return (bitmap != null) ? ConvertBitmap.ToBitmapSource(bitmap) : null;
        }

        private static BitmapSource LoadPNGGlyph(byte[] bytes)
        {
            var bitmap = ConvertPNG(bytes);
            return (bitmap != null) ? ConvertBitmap.ToBitmapSource(bitmap) : null;
        }

        private static BitmapSource LoadSVGGlyph(byte[] bytes, Size targetSize)
        {
            var bitmap = ConvertSVG(bytes, targetSize);
            return (bitmap != null) ? ConvertBitmap.ToBitmapSource(bitmap) : null;
        }

        private static Bitmap ConvertPNG(byte[] bytes)
        {
            try
            {
                Bitmap bitmap;
                using (var ms = new MemoryStream(bytes))
                {
                    bitmap = new Bitmap(ms);
                }

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap ConvertSVG(string path, Size targetSize)
        {
            try
            {
                return ConvertSVG(File.ReadAllBytes(path), targetSize);
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap ConvertSVG(byte[] bytes, Size targetSize)
        {
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var svg = new SkiaSharp.Extended.Svg.SKSvg();
                    var picture = svg.Load(stream);
                    var width = (int)svg.CanvasSize.Width;
                    var height = (int)svg.CanvasSize.Height;

                    var scale = 1.0f;
                    if (targetSize != Size.Empty)
                    {
                        var widthRatio = targetSize.Width / (float)width;
                        var heightRatio = targetSize.Height / (float)height;
                        scale = Math.Min(widthRatio, heightRatio);
                        width = (int)(width * scale);
                        height = (int)(height * scale);
                    }

                    var image = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, image.PixelFormat);

                    var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                    using (var surface = SKSurface.Create(info, data.Scan0, width * 4))
                    {
                        var skcanvas = surface.Canvas;
                        skcanvas.Scale(scale);
                        skcanvas.DrawPicture(picture);
                    }

                    image.UnlockBits(data);

                    return image;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetAspectName(string fullName)
        {
            return fullName.Split('.').Last().Replace("Aspect", String.Empty);
        }

        private static string GetAspectDisplayName(AspectLibrary.Library.Class aspect)
        {
            if (String.IsNullOrWhiteSpace(aspect.DisplayName))
            {
                return Wordify(GetAspectName(aspect.FullName));
            }

            return aspect.DisplayName;
        }

        private static string GetAspectCategoryDisplayName(AspectLibrary.Library.Class aspect)
        {
            if (String.IsNullOrWhiteSpace(aspect.Category))
            {
                return GetAspectDisplayName(aspect);
            }
            else
            {
                return aspect.Category.Split('/').Last();
            }
        }

        private static string Wordify(string str)
        {
            return Regex.Replace(str, @"([a-z](?=[A-Z])|[A-Z](?=[A-Z][a-z]))", "$1 ");
        }

        private void CreateAspect(Selection selection, Type aspectType)
        {
            if (selection != null)
            {
                foreach (var visual in selection)
                {
                    if (visual != null)
                    {
                        var aspect = (AspectComponentBase)Activator.CreateInstance(aspectType);
                        visual.AddAspect(aspect);
                        AddScripts(visual);
                        AspectViewerDockPane.ValidateAspect(app, aspect, true);
                        aspect.IsCollapsed = false;
                    }
                }

                AspectViewerDockPane.Show(app, selection, true);
            }
        }

        private void AddScripts(Visual visual)
        {
            var documentLibrary = AspectLibraryPlugin.GetAspectLibrary(this.app.Document.Resources);

            var missingAspects = new List<string>();
            foreach (var aspect in visual.AllAspects)
            {
                var aspectType = aspect.GetType();
                var aspectTypeName = aspectType.FullName;
                var assemblyName = aspectType.Assembly.GetName();
                if (documentLibrary.FindType(aspectTypeName) == null)
                {
                    if (assemblyName == null || assemblyName.Name != "Demo3D.Core")
                    {
                        missingAspects.Add(aspectTypeName);
                    }
                }
            }

            if (missingAspects.Count > 0)
            {
                foreach (var catalog in this.app.CatalogManager.Catalogs)
                {
                    var catalogLibrary = AspectLibraryPlugin.GetAspectLibrary(catalog.Resources);
                    for (int i = 0; i < missingAspects.Count; ++i)
                    {
                        var missingAspectName = missingAspects[i];
                        var missingAspectType = catalogLibrary.FindType(missingAspectName);
                        if (missingAspectType != null)
                        {
                            missingAspects.RemoveAt(i--);

                            var script = catalog.Resources.Scripts.First(s => s.NativeAssembly == missingAspectType.Assembly);
                            var referencedScripts = script.GetScriptReferences(catalog.Resources.Scripts);
                            foreach (var referencedScript in referencedScripts)
                            {
                                if (this.app.Document.Resources.Scripts.Contains(referencedScript.Key) == false)
                                {
                                    this.app.Document.Resources.Scripts[referencedScript.Key] = referencedScript;
                                }
                            }
                        }
                    }

                    if (missingAspects.Count <= 0) { break; }
                }

                foreach (var missingAspectName in missingAspects)
                {
                    this.app.LogMessage("Warning", $"No script found for aspect '{missingAspectName}'", visual);
                }
            }
        }

        public override void Start()
        {
        }

        public override void Stop()
        {
        }
    }
}