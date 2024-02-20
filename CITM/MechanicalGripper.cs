using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;

using Microsoft.DirectX;

using Demo3D.Common;
using Demo3D.Visuals;
using Demo3D.Utilities;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;

namespace Demo3D.Components
{
    using Properties;

    public enum MechanicalGripperGripMode
    {
        Reparenting,
        FixedJoint
    }

    public enum MechanicalGripperControlMode
    {
        None,
        ObjectDetection
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.EndEffectors_Category))]
    [HelpUrl("mechanical_gripper")]
    public class MechanicalGripper : ExportableVisualAspect, IBindableItemOwner
    {
        private MechanicalGripperControlMode controlMode = MechanicalGripperControlMode.None;
        private MechanicalGripperGripMode gripMode = MechanicalGripperGripMode.Reparenting;
        private VisualPolygonFace[] surfaces = new VisualPolygonFace[4];
        private List<Visual> sensorVisuals = new List<Visual>();
        private List<CollisionSensorAspect> hookedSensors = new List<CollisionSensorAspect>();
        private Visual bodyVisual = null;
        private double sensorDepth = 0.01;
        private BindableItem objectDetectedBindableItem = null;
        private object lockObj = new object();

        private const string BodyVisualName = "GripperBody";
        private const string SensorVisualName = "GripperSensor";

        [AspectProperty]
        [DefaultValue(SuctionGripperControlMode.None)]
        public MechanicalGripperControlMode ControlMode
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

        [AspectProperty]
        [DefaultValue(MechanicalGripperGripMode.Reparenting)]
        public MechanicalGripperGripMode GripMode
        {
            get { return gripMode; }
            set
            {
                if (gripMode != value)
                {
                    ReleaseAll();
                    gripMode = value;
                    GripReleaseAll();
                    RaisePropertyChanged(nameof(GripMode));
                }
            }
        }

        [AspectProperty]
        [Required]
        public VisualPolygonFace Surface1
        {
            get { return surfaces[0]; }
            set
            {
                if (SetProperty(ref surfaces[0], value))
                {
                    FindCreateSensorVisuals();
                }
            }
        }

        [AspectProperty]
        public VisualPolygonFace Surface2
        {
            get { return surfaces[1]; }
            set
            {
                if (SetProperty(ref surfaces[1], value))
                {
                    FindCreateSensorVisuals();
                }
            }
        }

        [AspectProperty]
        public VisualPolygonFace Surface3
        {
            get { return surfaces[2]; }
            set
            {
                if (SetProperty(ref surfaces[2], value))
                {
                    FindCreateSensorVisuals();
                }
            }
        }

        [AspectProperty]
        public VisualPolygonFace Surface4
        {
            get { return surfaces[3]; }
            set
            {
                if (SetProperty(ref surfaces[3], value))
                {
                    FindCreateSensorVisuals();
                }
            }
        }

        [AspectProperty]
        [XmlIgnore]
        public bool ObjectDetected
        {
            get { return GrippedVisuals.Count > 0; }
        }

        [AspectProperty(IsReadOnly = true)]
        [XmlIgnore]
        public VisualList GrippedVisuals { get; } = new VisualList();

        [AspectProperty]
        [Distance, DefaultValue(0.01)]
        public double SensorDepth
        {
            get { return sensorDepth; }
            set
            {
                var sgn = (value >= 0) ? 1.0 : -1.0;
                if (SetProperty(ref sensorDepth, sgn * Math.Max(0.001, Math.Abs(value))))
                {
                    FindCreateSensorVisuals();
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem ObjectDetectedBindableItem
        {
            get { return objectDetectedBindableItem; }
            private set
            {
                objectDetectedBindableItem = value;
                objectDetectedBindableItem.Value = ObjectDetected;
                objectDetectedBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems
        {
            get
            {
                if (ObjectDetectedBindableItem != null) yield return ObjectDetectedBindableItem;
            }
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();

            CleanupBindingAPI();

            if (Visual != null)
            {
                CreateBindings();
                UpdateBindings();
            }
        }

        protected override void OnAdded()
        {
            base.OnAdded();

            FindCreateSensorVisuals();
            FindCreateBodyVisual();
        }

        protected override void OnRemoved()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem?.DetachFromVisual();
            }

            RemoveSensorVisuals();
            RemoveBodyVisual();

            base.OnRemoved();
        }

        protected override void OnReset()
        {
            base.OnReset();
            ReleaseAll(true);
        }

        private void ReleaseAll(bool resetPosition = false)
        {
            for (int i = GrippedVisuals.Count - 1; i >= 0; --i)
            {
                var visualReference = GrippedVisuals[i];
                if (visualReference != null)
                {
                    var visual = visualReference.Visual;
                    Release(visual);

                    if (resetPosition)
                    {
                        if (visual.InitialParentOnReset != null)
                        {
                            var parent = visual.InitialParentOnReset.Visual;
                            if (parent != null)
                            {
                                visual.Parent = parent;
                            }
                        }

                        if (visual.InitialPositionOnReset)
                        {
                            visual.GoToInitialPosition();
                        }
                    }
                }
            }
        }

        private void Release(Visual visual)
        {
            if (visual != null)
            {
                if (RemoveGrippedVisual(visual))
                {
                    if (visual is PhysicsObject physicsObject)
                    {
                        if (gripMode == MechanicalGripperGripMode.FixedJoint)
                        {
                            app.LogMessage("Warning", "Core visuals cannot be gripped/released using fixed joints. Falling back to using reparenting.", Visual);
                        }

                        var initialMatrix = physicsObject.InitialMatrix;
                        physicsObject.Parent = null;
                        physicsObject.InitialMatrix = initialMatrix;
                        physicsObject.Kinematic = false;
                        physicsObject.WakeupPhysics();
                    }
                    else
                    {
                        var rigidBody = visual.FindAspect<RigidBodyAspect>();
                        if (rigidBody != null)
                        {
                            switch (gripMode)
                            {
                                case MechanicalGripperGripMode.Reparenting:
                                    var initialMatrix = visual.InitialMatrix;
                                    visual.Parent = null;
                                    visual.InitialMatrix = initialMatrix;
                                    rigidBody.Kinematic = false;
                                    rigidBody.WakeUp();
                                    break;
                                case MechanicalGripperGripMode.FixedJoint:
                                    foreach (var fixedJoint in new List<DynamicFixedJointAspect>(visual.FindAspects<DynamicFixedJointAspect>()))
                                    {
                                        if (fixedJoint.OtherVisual == bodyVisual)
                                        {
                                            visual.RemoveAspect(fixedJoint);
                                            visual.WakeupPhysics();
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }
        private bool Grip(Visual visual)
        {
            if (visual != null)
            {
                if (GrippedVisuals.Contains(visual) == false)
                {
                    if (visual is PhysicsObject physicsObject)
                    {
                        if (physicsObject.BodyType == PhysicsBodyType.Load)
                        {
                            if (gripMode == MechanicalGripperGripMode.FixedJoint)
                            {
                                app.LogMessage("Warning", "Core visuals cannot be gripped/released using fixed joints. Falling back to using reparenting.", Visual);
                            }

                            var initialMatrix = physicsObject.InitialMatrix;
                            physicsObject.Kinematic = true;
                            physicsObject.Parent = bodyVisual;
                            physicsObject.InitialMatrix = initialMatrix;

                            return AddGrippedVisual(physicsObject);
                        }
                    }
                    else
                    {
                        var rigidBodyAspect = visual.FindAspect<RigidBodyAspect>();
                        var loadAspect = visual.FindAspect<LoadAspect>();
                        if (rigidBodyAspect != null && loadAspect != null)
                        {
                            switch (gripMode)
                            {
                                case MechanicalGripperGripMode.Reparenting:
                                    var initialMatrix = visual.InitialMatrix;
                                    rigidBodyAspect.Kinematic = true;
                                    visual.Parent = bodyVisual;
                                    visual.InitialMatrix = initialMatrix;
                                    break;
                                case MechanicalGripperGripMode.FixedJoint:
                                    {
                                        var fixedJoint = new DynamicFixedJointAspect();
                                        fixedJoint.OtherVisual = bodyVisual;
                                        visual.AddAspect(fixedJoint);
                                    }
                                    break;
                            }

                            return AddGrippedVisual(visual);
                        }
                    }
                }
            }

            return false;
        }

        private void GripReleaseAll()
        {
            // May be called from multiple threads.
            lock (lockObj)
            {
                // We iterate through the sensors and count how many sensors each blocking visual is
                // seen by.
                var loadCounts = new Dictionary<Visual, int>();
                foreach (var sensorAspect in hookedSensors)
                {
                    foreach (var blockingVisualReference in sensorAspect.BlockingVisuals)
                    {
                        var visual = blockingVisualReference?.Visual ?? null;
                        if (visual is PhysicsObject physicsObject)
                        {
                            if (physicsObject.BodyType == PhysicsBodyType.Load)
                            {
                                loadCounts.TryGetValue(physicsObject, out var count);
                                loadCounts[physicsObject] = count + 1;
                            }
                        }
                        else if (visual != null)
                        {
                            var rigidBodyAspect = visual.FindAspect<RigidBodyAspect>();
                            var loadAspect = visual.FindAspect<LoadAspect>();
                            if (rigidBodyAspect != null && loadAspect != null)
                            {
                                loadCounts.TryGetValue(visual, out var count);
                                loadCounts[visual] = count + 1;
                            }
                        }
                    }
                }

                // If a load is seen by all sensors then grip it, otherwise release it.
                foreach (var kv in loadCounts)
                {
                    if (kv.Value >= hookedSensors.Count)
                    {
                        Grip(kv.Key);
                    }
                    else
                    {
                        Release(kv.Key);
                    }
                }
            }
        }

        private bool AddGrippedVisual(Visual visual)
        {
            if (GrippedVisuals.Contains(visual) == false)
            {
                int previousCount = GrippedVisuals.Count;
                GrippedVisuals.Add(visual);

                if (previousCount <= 0 && GrippedVisuals.Count > 0)
                {
                    ObjectDetectedBindableItem.Value = true;
                    RaisePropertyChanged(nameof(ObjectDetected));
                }

                RaisePropertyChanged(nameof(GrippedVisuals));
                return true;
            }

            return false;
        }

        private bool RemoveGrippedVisual(Visual visual)
        {
            int previousCount = GrippedVisuals.Count;
            if (GrippedVisuals.Remove(visual))
            {
                if (GrippedVisuals.Count <= 0)
                {
                    ObjectDetectedBindableItem.Value = false;
                    RaisePropertyChanged(nameof(ObjectDetected));
                }

                RaisePropertyChanged(nameof(GrippedVisuals));
                return true;
            }

            return false;
        }

        private void RemoveBodyVisual()
        {
            // Release any gripped visuals.
            ReleaseAll();

            // Remove any existing body visual.
            if (bodyVisual != null)
            {
                bodyVisual.Delete();
                bodyVisual = null;
            }
        }

        private void FindCreateBodyVisual()
        {
            // Remove any existing body visual.
            RemoveBodyVisual();

            bodyVisual = Visual.FindImmediateChild(BodyVisualName);
            if (bodyVisual == null)
            {
                // Create the body visual and parent it to this visual.
                bodyVisual = document.CreateVisual<Visual>();
                bodyVisual.Name = BodyVisualName;
                bodyVisual.Parent = Visual;
                bodyVisual.Location = Vector3.Zero;
            }

            // The body visual requires only a rigid body aspect, but we must ensure that it is
            // marked as being kinematic.
            var rigidBody = bodyVisual.FindCreateAspect<RigidBodyAspect>();
            if (rigidBody != null)
            {
                rigidBody.Kinematic = true;
                rigidBody.AspectManagedBy = this;
            }
        }

        private void RemoveSensorVisuals()
        {
            // Release any gripped visuals.
            ReleaseAll();

            // Unhook any existing sensors.
            UnhookSensors();

            // Remove existing sensor visuals.
            foreach (var sensorVisual in sensorVisuals)
            {
                sensorVisual.Delete();
            }

            // Clear the list of sensor visuals.
            sensorVisuals.Clear();
        }

        private void FindCreateSensorVisuals()
        {
            // Remove any existing sensor visuals.
            RemoveSensorVisuals();

            // Create a sensor visual for each surface.
            foreach (var surface in surfaces)
            {
                if (surface != null && surface.Visual != null)
                {
                    var sensorVisual = surface.Visual.FindImmediateChild(SensorVisualName);
                    if (sensorVisual == null)
                    {
                        sensorVisual = document.CreateVisual<Visual>();
                        sensorVisual.Name = SensorVisualName;
                        sensorVisual.Parent = surface.Visual;
                        sensorVisual.InitialPositionOnReset = true;
                        sensorVisual.Matrix = Matrix.Identity;
                        sensorVisual.InitialMatrix = Matrix.Identity;
                    }

                    sensorVisuals.Add(sensorVisual);

                    // To create the physics geometry for the sensor we extrude the face in the
                    // direction of the normal.
                    var direction = surface.Normal * Math.Sign(sensorDepth);
                    var convexExtrusionAspect = sensorVisual.FindCreateAspect<ConvexExtrusionPhysicsAspect>();
                    convexExtrusionAspect.Profile = surface;
                    convexExtrusionAspect.Path = new VisualTessellatedCurve() { Visual = surface.Visual, Point = surface.Point, Normal = direction, Vertices = new Vector3[] { surface.Point, surface.Point + direction * Math.Abs(sensorDepth) } };
                    convexExtrusionAspect.StepLength = Math.Abs(sensorDepth) * 2.0; // Ensure that we only do one extrusion step.
                    convexExtrusionAspect.UseAncestorBody = false;
                    convexExtrusionAspect.AspectManagedBy = this;
                }
            }

            // Hook sensor aspects.
            HookSensors();
        }

        private CollisionSensorAspect FindCreateSensorAspect(Visual visual)
        {
            if (visual != null)
            {
                var sensorAspect = visual.FindCreateAspect<CollisionSensorAspect>();
                if (sensorAspect != null)
                {
                    sensorAspect.AspectManagedBy = this;
                    sensorAspect.ControlMode = CollisionSensorControlMode.None;
                    sensorAspect.SenseMultipleLoads = true;
                    return sensorAspect;
                }
            }

            return null;
        }

        private void UnhookSensors()
        {
            foreach (var sensorAspect in hookedSensors)
            {
                sensorAspect.OnBlocked -= OnSensorBlocked;
                sensorAspect.OnCleared -= OnSensorCleared;
            }

            hookedSensors.Clear();
        }

        private void HookSensors()
        {
            UnhookSensors();

            foreach (var sensorVisual in sensorVisuals)
            {
                var sensorAspect = FindCreateSensorAspect(sensorVisual);

                sensorAspect.OnBlocked -= OnSensorBlocked;
                sensorAspect.OnBlocked += OnSensorBlocked;

                sensorAspect.OnCleared -= OnSensorCleared;
                sensorAspect.OnCleared += OnSensorCleared;

                hookedSensors.Add(sensorAspect);
            }
        }

        private void OnSensorBlocked(Visual visual)
        {
            GripReleaseAll();
        }

        private void OnSensorCleared(Visual visual)
        {
            GripReleaseAll();
        }

        private void CreateBindings()
        {
            if (Visual == null) { return; }

            ObjectDetectedBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(ObjectDetected)));
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
                case MechanicalGripperControlMode.ObjectDetection:
                    ObjectDetectedBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
            }

            UpdateBindingAPI();
        }
    }
}
