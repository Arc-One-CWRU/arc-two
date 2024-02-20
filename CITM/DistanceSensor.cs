using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Runtime.CompilerServices;

using Microsoft.DirectX;

using Demo3D.Common;
using Demo3D.EventQueue;
using Demo3D.Native;
using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.Visuals.Renderers;
using Demo3D.Visuals.Renderers.Mesh;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;

using Geometry = Demo3D.Common.Geometry;

namespace Demo3D.Components
{
    using Properties;

    public enum DistanceSensorControlMode
    {
        None = 0,
        Distance = 1 << 0,
        TargetColorDistance = 1 << 1,
        All = Distance | TargetColorDistance
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Sensors_Category))]
    [HelpUrl("distance_sensor")]
    public class DistanceSensor : ExportableVisualAspect, IBindableItemOwner {
        private bool initialized;
        private CylinderVisual sensor;
        private ITask mainTask;
        private List<Ray> rays = new List<Ray>();

        private DistanceSensorControlMode controlMode = DistanceSensorControlMode.None;
        private double scanSeparation = 0.025;
        private double sampleRate = 5.0;
        private double range = 1.0;
        private double diameter = 0.2;
        private VisualNormal axis = null;
        private VisualPoint anchor = null;
        private ColorProperty color = null;

        private bool isBlocked = false;
        private double distance = -1;
        private double targetColorDistance = -1;

        private BindableItem isBlockedBindableItem;
        private BindableItem distanceBindableItem;
        private BindableItem targetColorDistanceBindableItem;

        [Required, DefaultValue(5)]
        public double SampleRate
        {
            get { return sampleRate; }
            set
            {
                SetProperty(ref sampleRate, value);
            }
        }

        [Distance, DefaultValue(0.025)]
        public double ScanSeparation
        {
            get { return scanSeparation; }
            set
            {
                if (SetProperty(ref scanSeparation, value))
                {
                    GenerateRays();
                }
            }
        }

        [Required, Distance, DefaultValue(1.0)]
        public double Range
        {
            get { return range; }
            set
            {
                if (SetProperty(ref range, value))
                {
                    ConfigureSensor();
                }
            }
        }

        [Required, Distance, DefaultValue(0.2)]
        public double Diameter
        {
            get { return diameter; }
            set
            {
                if (SetProperty(ref diameter, value))
                {
                    ConfigureSensor();
                }
            }
        }

        [Required]
        public VisualNormal Axis
        {
            get { return axis; }
            set
            {
                if (SetVisualLocation(ref axis, value))
                {
                    ConfigureSensor();
                }
            }
        }

        [Required]
        public VisualPoint Anchor
        {
            get { return anchor; }
            set
            {
                if (SetVisualLocation(ref anchor, value))
                {
                    ConfigureSensor();
                }
            }
        }

        [Required]
        public ColorProperty TargetColor
        {
            get { return color; }
            set
            {
                SetProperty(ref color, value);
            }
        }

        [DefaultValue(DistanceSensorControlMode.None)]
        public DistanceSensorControlMode ControlMode
        {
            get { return controlMode; }
            set
            {
                if (SetProperty(ref controlMode, value))
                {
                    UpdateBindings();
                }
            }
        }

        [XmlIgnore]
        public bool IsBlocked
        {
            get { return isBlocked; }
            private set
            {
                if (SetProperty(ref isBlocked, value))
                {
                    if (IsBlockedBindableItem != null) { IsBlockedBindableItem.Value = isBlocked; }
                }
            }
        }

        [XmlIgnore]
        public double Distance
        {
            get { return distance; }
            private set
            {
                if (SetProperty(ref distance, value))
                {
                    if (DistanceBindableItem != null) { DistanceBindableItem.Value = distance; }
                }
            }
        }

        [XmlIgnore]
        public double TargetColorDistance
        {
            get { return targetColorDistance; }
            private set
            {
                if (SetProperty(ref targetColorDistance, value))
                {
                    if (TargetColorDistanceBindableItem != null) { TargetColorDistanceBindableItem.Value = targetColorDistance; }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem IsBlockedBindableItem
        {
            get { return isBlockedBindableItem; }
            private set
            {
                isBlockedBindableItem = value;
                isBlockedBindableItem.Value = IsBlocked;
                isBlockedBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem DistanceBindableItem
        {
            get { return distanceBindableItem; }
            private set
            {
                distanceBindableItem = value;
                distanceBindableItem.Value = Distance;
                distanceBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem TargetColorDistanceBindableItem
        {
            get { return targetColorDistanceBindableItem; }
            private set
            {
                targetColorDistanceBindableItem = value;
                targetColorDistanceBindableItem.Value = TargetColorDistance;
                targetColorDistanceBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems
        {
            get
            {
                if (IsBlockedBindableItem != null) { yield return IsBlockedBindableItem; }
                if (DistanceBindableItem != null) { yield return DistanceBindableItem; }
                if (TargetColorDistanceBindableItem != null) { yield return TargetColorDistanceBindableItem; }
            }
        }

        #region VisualAspect
        protected override void OnEnabled()
        {
            base.OnEnabled();
            if (initialized) { Start(); }
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            Stop();
        }

        protected override void OnRemoved()
        {
            StopMainLoop();
            RemoveSensor();

            base.OnRemoved(); 
        }

        protected override void OnAdded()
        {
            CreateBindings();
            UpdateBindings();

            ConfigureSensor();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            if (IsEnabled) { Start(); }
            initialized = true;
        }

        private void CreateBindings()
        {
            if (Visual == null) { return; }

            IsBlockedBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsBlocked)));
            DistanceBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Distance)));
            TargetColorDistanceBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(TargetColorDistance)));
        }

        private void UpdateBindings()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem.IsBindingInterface = TriStateYNM.No;
            }

            if (Visual == null) { return; }

            switch (ControlMode)
            {
                case DistanceSensorControlMode.Distance:
                    if (IsBlockedBindableItem != null) { IsBlockedBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    if (DistanceBindableItem != null) { DistanceBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    break;
                case DistanceSensorControlMode.TargetColorDistance:
                    if (IsBlockedBindableItem != null) { IsBlockedBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    if (TargetColorDistanceBindableItem != null) { TargetColorDistanceBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    break;
                case DistanceSensorControlMode.All:
                    if (IsBlockedBindableItem != null) { IsBlockedBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    if (DistanceBindableItem != null) { DistanceBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    if (TargetColorDistanceBindableItem != null) { TargetColorDistanceBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    break;
            }

            UpdateBindingAPI();
        }

        protected override void OnReset()
        {
            base.OnReset();

            Stop();

            if (SensorChild is CylinderVisual sensor)
            {
                sensor.Material.Color = System.Drawing.Color.Violet;
            }
        }

        #endregion

        void Start()
        {
            Sensor.PhysicsEnabled = true;
            StartMainLoop();
        }

        void Stop()
        {
            Sensor.PhysicsEnabled = false;
            StopMainLoop();
        }

        void StartMainLoop()
        {
            StopMainLoop();
            mainTask = MainLoop().ToTask().IfException(x => { });
        }

        void StopMainLoop()
        {
            if (mainTask != null)
            {
                mainTask.Cancel(new TaskException("Cancelled"));
                mainTask = null;
            }
        }

        private IEnumerable MainLoop()
        {
            while (true)
            {
                if (!Sensor.IsBlocked)
                {
                    IsBlocked = false;
                    Distance = -1;
                    TargetColorDistance = -1;
                    Sensor.Material.Color = System.Drawing.Color.Violet;
                }

                yield return Wait.UntilTrue(() => Sensor.IsBlocked && SampleRate > 0, Sensor);

                Scan();

                yield return Wait.ForSeconds(1.0 / Math.Min(1000.0,SampleRate));
            }
        }

        private static double Sq(double v) { return v * v; }

        private void Scan()
        {
            if (rays == null || rays.Count <= 0) { return; }

            var minDistance = Double.PositiveInfinity;
            PickInfo minPickInfo = null;

            foreach (var blockingLoad in Sensor.BlockingLoads)
            {
                if (blockingLoad is Visual targetVisual)
                {
                    foreach (var localRay in rays)
                    {
                        var worldRay = new Ray() { Position = Visual.TransformToWorld(localRay.Position), Direction = Visual.TransformToWorldNormal(localRay.Direction) };
                        var pickInfo = new PickInfo();
                        if (Pick(worldRay, targetVisual, ref pickInfo) && pickInfo.Distance <= Range && pickInfo.Distance < minDistance)
                        {
                            minDistance = pickInfo.Distance;
                            minPickInfo = pickInfo;
                        }
                    }
                }
            }

            if (minPickInfo != null)
            {
                Measure(minPickInfo);
                Sensor.Material.Color = System.Drawing.Color.Purple;
                return;
            }            

            IsBlocked = false;
            Distance = -1;
            TargetColorDistance = -1;
        }

        private static readonly double MaxColorDistance = Math.Sqrt(3) * 255;

        private void Measure(PickInfo info)
        {
            IsBlocked = true;
            Distance = info.Distance;

            if (TargetColor != null && info.Subset >= 0)
            {
                System.Drawing.Color? color = null;

                if (info.ActualRenderablePicked is MeshRenderable meshRenderable)
                {
                    if (meshRenderable.MaterialProperties.Length > info.Subset)
                    {
                        color = meshRenderable.MaterialProperties[info.Subset].Color;
                    }
                }
                else if (info.ActualRendererAspectPicked is IMaterialContainerAspect materialContainer)
                {
                    if (materialContainer.Materials.Length > info.Subset)
                    {
                        color = materialContainer.Materials[info.Subset].Diffuse;
                    }
                }
                else if (info.ActualVisualPicked is MeshObject meshObject)
                {
                    if (meshObject.MeshMaterials.Length > info.Subset)
                    {
                        color = meshObject.MeshMaterials[info.Subset].Diffuse;
                    }
                }

                if (color.HasValue)
                {
                    var value = color.Value;
                    TargetColorDistance = Math.Sqrt(
                        Sq(value.R - TargetColor.R) +
                        Sq(value.G - TargetColor.G) +
                        Sq(value.B - TargetColor.B)
                    ) / MaxColorDistance;
                }
            }
            else
            {
                TargetColorDistance = -1;
            }
        }

        private bool Pick(Ray worldRay, Visual targetVisual, ref PickInfo pickInfo)
        {
            pickInfo.WantSubset = true;
            return targetVisual.Pick(app.Display, worldRay, ref pickInfo);
        }

        #region Sensor Management
        const string SensorName = "DistanceSensorZone";

        private Visual SensorChild { get { return Visual.FindImmediateChild(SensorName); } }

        private CylinderVisual Sensor {
            get {
                return sensor ?? (sensor = (SensorChild as CylinderVisual ?? CreateSensor()));
            }
        }

        private void RemoveSensor()
        {
            SensorChild.Do(v => document.DestroyVisual(v));
            sensor = null;
        }

        private void ConfigureSensor()
        {
            if (Visual == null || axis == null || axis.Normal == Vector3.Zero || anchor == null) { return; }

            var from = Anchor.WorldLocation;
            var to   = Anchor.WorldLocation + Vector3.Normalize(Axis.WorldNormal) * range;

            Sensor.Length = Range;
            Sensor.Diameter = Diameter;
            Sensor.WorldMatrix = Matrix.RotationZ270 * Util.VectorToRotationUp(axis.WorldNormal, GetAnyNonParallelVector(axis.WorldNormal)) * Matrix.Translation((to + from) * 0.5);

            GenerateRays();
        }

        private void GenerateRays()
        {
            rays.Clear();

            if (Visual == null || Axis == null || Axis.Normal == Vector3.Zero || Anchor == null) { return; }

            var radius = diameter * 0.5;
            var localOrigin = Geometry.Conversion.Convert(Visual.TransformFromWorld(anchor.WorldLocation));
            var localNormal = Geometry.Conversion.Convert(Visual.TransformFromWorldNormal(axis.WorldNormal));
            var localPlane = Geometry.Plane3D.FromPointNormal(localOrigin + localNormal * range, localNormal);

            int numRings = Math.Max(0, (int)Math.Round(radius / ScanSeparation));
            var radiusIncrement = radius / numRings;

            for (int ring = 0; ring <= numRings; ++ring)
            {
                if (ring == 0)
                {
                    rays.Add(new Ray() { Position = Geometry.Conversion.Convert(localOrigin), Direction = Geometry.Conversion.Convert(localNormal) });
                    continue;
                }

                var localCircle = new Geometry.Circle3D(localPlane, radiusIncrement * ring);
                var numSteps = (int)Math.Ceiling(localCircle.Length / ScanSeparation);
                var stepLength = localCircle.Length / numSteps;

                for (int step = 0; step < numSteps; ++step)
                {
                    var localDirection = Geometry.Vector3D.Direction(localOrigin, localCircle.Point(stepLength * step));
                    rays.Add(new Ray() { Position = Geometry.Conversion.Convert(localOrigin), Direction = Geometry.Conversion.Convert(localDirection) });
                }
            }
        }

        private static Vector3 GetAnyNonParallelVector(Vector3 v)
        {
            return Util.NormalsAreParallel(v, Vector3.YAxis) ? Vector3.XAxis : Vector3.YAxis;
        }

        private CylinderVisual CreateSensor()
        {
            var v = document.CreateVisual<CylinderVisual>();
            v.Name = SensorName;
            v.BodyType = PhysicsBodyType.Sensor;
            v.SenseMultipleLoads = true;
            v.Visible = true;
            v.Diameter = Diameter;
            v.Length = Range;
            v.ConeRatio = 0;
            v.Material.Color = System.Drawing.Color.Violet;
            v.Material.Transparency = 0.5;
            v.Reparent(this.Visual);
            v.ShowControlPoints = false;
            v.CastsShadow = false;
            v.Props.Selectable = false;
            return v;
        }
        #endregion

        private bool SetVisualLocation<T>(ref T storage, T value, [CallerMemberName] string propertyName = "") where T : VisualLocation
        {
            VisualLocation oldVisualLocation = storage;

            if (value != null && Visual != null)
            {
                value.ChangeVisual(Visual);
            }

            if (SetProperty(ref storage, value, new PropertyChangedEventArgs(propertyName)))
            {
                Unsubscribe(oldVisualLocation);
                Subscribe(storage);
                return true;
            }

            return false;
        }

        private void Subscribe(VisualLocation visualLocation)
        {
            if (visualLocation != null)
            {
                visualLocation.PropertyChanged -= VisualLocation_PropertyChanged;
                visualLocation.PropertyChanged += VisualLocation_PropertyChanged;
            }
        }

        private void Unsubscribe(VisualLocation visualLocation)
        {
            if (visualLocation != null)
            {
                visualLocation.PropertyChanged -= VisualLocation_PropertyChanged;
            }
        }

        private void VisualLocation_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ConfigureSensor();
        }
    }
}

