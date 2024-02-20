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

    public enum SuctionGripperControlMode
    {
        None,
        OnOff
    }

    [Flags]
    public enum SuctionGripperOutputs
    {
        None = 0,
        IsGripping = 1 << 0
    }

    public enum SuctionGripperGripMode
    {
        Reparenting,
        FixedJoint
    }

    [Flags]
    public enum SuctionGripperGripFilter
    {
        None = 0,
        DynamicBodies = 1 << 0,
        KinematicBodies = 1 << 1,
        Loads = 1 << 2,
        Default = DynamicBodies | Loads
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.EndEffectors_Category))]
    [HelpUrl("suction_gripper")]
    public class SuctionGripper : ExportableVisualAspect, IBindableItemOwner
    {
        private SuctionGripperControlMode controlMode = SuctionGripperControlMode.None;
        private SuctionGripperGripMode gripMode = SuctionGripperGripMode.Reparenting;
        private SuctionGripperGripFilter gripFilter = SuctionGripperGripFilter.Default;
        private SuctionGripperOutputs outputs = SuctionGripperOutputs.None;
        private bool state = false;
        private bool massLimitEnabled = false;
        private double massLimit = 10;
        private bool gripping = false;
        private CollisionSensorAspect hookedSensor = null;
        private Visual gripperBodyVisual = null;

        private BindableItem stateBindableItem = null;
        private BindableItem massLimitBindableItem = null;
        private BindableItem grippingBindableItem = null;

        private const string GripperBodyVisualName = "GripperBody";

        [AspectProperty]
        [DefaultValue(SuctionGripperControlMode.None)]
        public SuctionGripperControlMode ControlMode
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
        [DefaultValue(SuctionGripperOutputs.None)]
        public SuctionGripperOutputs Outputs
        {
            get { return outputs; }
            set
            {
                if (SetProperty(ref outputs, value))
                {
                    UpdateBindings();

                    RaisePropertyChanged(nameof(OutputIsGripping));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool OutputIsGripping
        {
            get { return outputs.HasFlag(SuctionGripperOutputs.IsGripping); }
        }

        [AspectProperty]
        [DefaultValue(SuctionGripperGripMode.Reparenting)]
        public SuctionGripperGripMode GripMode
        {
            get { return gripMode; }
            set
            {
                if (gripMode != value)
                {
                    ReleaseAll();
                    gripMode = value;
                    GripAll();
                    RaisePropertyChanged(nameof(GripMode));
                }
            }
        }

        [AspectProperty]
        [DefaultValue(SuctionGripperGripFilter.Default)]
        public SuctionGripperGripFilter GripFilter
        {
            get { return gripFilter; }
            set
            {
                if (SetProperty(ref gripFilter, value))
                {
                    ReleaseAll();
                    GripAll();
                }
            }
        }

        [AspectProperty]
        [DefaultValue(false)]
        public bool MassLimitEnabled
        {
            get { return massLimitEnabled; }
            set
            {
                if (SetProperty(ref massLimitEnabled, value))
                {
                    ReleaseHeavy();
                    GripAll();

                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(MassLimitEnabled), IsEnabledPropertyLink = nameof(MassLimitEnabled))]
        [DefaultValue(10.0)]
        [Mass]
        public double MassLimit
        {
            get { return massLimit; }
            set
            {
                if (SetProperty(ref massLimit, Math.Max(0, value)))
                {
                    if (MassLimitBindableItem != null)
                    {
                        MassLimitBindableItem.Value = massLimit;
                    }

                    ReleaseHeavy();
                    GripAll();
                }
            }
        }

        [AspectProperty]
        [DefaultValue(false)]
        public bool State
        {
            get { return state; }
            set
            {
                if (SetProperty(ref state, value))
                {
                    if (StateBindableItem != null)
                    {
                        StateBindableItem.Value = state;
                    }

                    if (state)
                    {
                        GripAll();
                    }
                    else
                    {
                        ReleaseAll();
                    }
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(OutputIsGripping), IsEnabledPropertyLink = nameof(OutputIsGripping))]
        public bool IsGripping
        {
            get { return gripping; }
            private set
            {
                if (IsGrippingBindableItem != null)
                {
                    if (IsGrippingBindableItem.ValueAs<bool>() != value)
                    {
                        IsGrippingBindableItem.Value = value;
                    }
                }

                SetProperty(ref gripping, value);
            }
        }

        [AspectProperty(IsReadOnly = true)]
        [XmlIgnore]
        public VisualList GrippedVisuals { get; } = new VisualList();

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem StateBindableItem
        {
            get { return stateBindableItem; }
            private set
            {
                stateBindableItem = value;
                stateBindableItem.Value = State;
                stateBindableItem.ValueChanged += StateBindableItem_ValueSourceChangedListeners;
                stateBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem MassLimitBindableItem
        {
            get { return massLimitBindableItem; }
            private set
            {
                massLimitBindableItem = value;
                massLimitBindableItem.Value = MassLimit;
                massLimitBindableItem.ValueChanged += MassLimitBindableItem_ValueSourceChangedListeners;
                massLimitBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem IsGrippingBindableItem
        {
            get { return grippingBindableItem; }
            private set
            {
                if (grippingBindableItem != value)
                {
                    var gripping = IsGripping;

                    if (grippingBindableItem != null)
                    {
                        grippingBindableItem.ValueChanged -= OnIsGrippingBindableItemChanged;
                        grippingBindableItem.DetachFromVisual();
                        grippingBindableItem = null;
                    }

                    grippingBindableItem = value;
                    if (grippingBindableItem != null)
                    {
                        grippingBindableItem.ValueChanged += OnIsGrippingBindableItemChanged;
                        grippingBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                        grippingBindableItem.Value = gripping;
                    }
                }
            }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems
        {
            get
            {
                if (StateBindableItem != null) yield return StateBindableItem;
                if (MassLimitBindableItem != null) yield return MassLimitBindableItem;
                if (IsGrippingBindableItem != null) yield return IsGrippingBindableItem;
            }
        }

        void StateBindableItem_ValueSourceChangedListeners(BindableItem item)
        {
            State = item.ValueAs<bool>();
        }

        void MassLimitBindableItem_ValueSourceChangedListeners(BindableItem item)
        {
            MassLimit = item.ValueAs<double>();
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

            // Ensure that the visual has physics geometry.
            var geometry = Visual.FindAspect<PhysicsGeometryAspect>();
            if (geometry == null)
            {
                geometry = Visual.FindCreateAspect<BoxPhysicsAspect>();
                geometry.AspectManagedBy = this;
            }

            // Add the gripper body visual.
            FindCreateGripperBodyVisual();

            // Find/create and hook sensor.
            HookSensor();
        }

        protected override void OnRemoved()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem?.DetachFromVisual();
            }

            RemoveGripperBodyVisual();
            UnhookSensor();

            base.OnRemoved();
        }

        protected override void OnReset()
        {
            base.OnReset();
            ReleaseAll();
        }

        private void FindCreateGripperBodyVisual()
        {
            RemoveGripperBodyVisual();

            gripperBodyVisual = Visual.FindImmediateChild(GripperBodyVisualName);
            if (gripperBodyVisual == null)
            {
                gripperBodyVisual = document.CreateVisual<Visual>();
                gripperBodyVisual.Name = GripperBodyVisualName;
                gripperBodyVisual.Parent = Visual;
                gripperBodyVisual.Location = Vector3.Zero;
            }

            var rigidBody = gripperBodyVisual.FindCreateAspect<RigidBodyAspect>();
            rigidBody.Kinematic = true;
            rigidBody.AspectManagedBy = this;
        }

        private void RemoveGripperBodyVisual()
        {
            if (gripperBodyVisual != null)
            {
                ReleaseAll();
                gripperBodyVisual.Delete();
                gripperBodyVisual = null;
            }
        }

        private CollisionSensorAspect FindCreateSensor()
        {
            var sensor = Visual.FindAspect<CollisionSensorAspect>();
            if (sensor == null)
            {
                sensor = Visual.FindCreateAspect<CollisionSensorAspect>();
                if (sensor != null)
                {
                    sensor.ControlMode = CollisionSensorControlMode.None;
                    sensor.SenseMultipleLoads = true;
                    sensor.AspectManagedBy = this;
                }
            }

            return sensor;
        }

        private void HookSensor()
        {
            HookSensor(FindCreateSensor());
        }

        private void HookSensor(CollisionSensorAspect sensor)
        {
            if (sensor != null)
            {
                UnhookSensor(); // Unhook any existing sensor.

                hookedSensor = sensor;

                sensor.OnBlocked -= OnSensorBlocked;
                sensor.OnBlocked += OnSensorBlocked;
            }
        }

        private void UnhookSensor()
        {
            if (hookedSensor != null)
            {
                hookedSensor.OnBlocked -= OnSensorBlocked;
                hookedSensor = null;
            }
        }

        private void OnSensorBlocked(Visual blockedVisual)
        {
            if (State)
            {
                Grip(blockedVisual);
            }
        }

        private void GripAll()
        {
            if (hookedSensor != null)
            {
                var blockingVisuals = hookedSensor.BlockingVisuals.Visuals.ToList();
                foreach (var blockingVisual in blockingVisuals)
                {
                    Grip(blockingVisual);
                }
            }
        }

        private bool CanGrip(PhysicsObject physicsObject)
        {
            if (MassLimitEnabled && physicsObject.Mass > MassLimit)
            {
                return false;
            }

            if ((gripFilter & SuctionGripperGripFilter.DynamicBodies) != 0 && physicsObject.Kinematic == false)
            {
                return true;
            }

            if ((gripFilter & SuctionGripperGripFilter.KinematicBodies) != 0 && physicsObject.Kinematic)
            {
                return true;
            }

            if ((gripFilter & SuctionGripperGripFilter.Loads) != 0 && physicsObject.BodyType == PhysicsBodyType.Load)
            {
                return true;
            }

            return false;
        }

        private bool CanGrip(RigidBodyAspect rigidBodyAspect)
        {
            if (rigidBodyAspect != null && rigidBodyAspect.IsEnabled)
            {
                if (MassLimitEnabled && rigidBodyAspect.Mass > MassLimit)
                {
                    return false;
                }

                if ((gripFilter & SuctionGripperGripFilter.DynamicBodies) != 0 && rigidBodyAspect.Kinematic == false)
                {
                    return true;
                }

                if ((gripFilter & SuctionGripperGripFilter.KinematicBodies) != 0 && rigidBodyAspect.Kinematic)
                {
                    return true;
                }

                if ((gripFilter & SuctionGripperGripFilter.Loads) != 0)
                {
                    var visual = rigidBodyAspect.Visual;
                    if (visual != null)
                    {
                        var loadAspect = visual.FindAspect<LoadAspect>();
                        return loadAspect != null && loadAspect.IsEnabled;
                    }
                }
            }
            
            return false;
        }

        private bool Grip(Visual visual)
        {
            if (visual != null)
            {
                if (GrippedVisuals.Contains(visual) == false)
                {
                    if (visual is PhysicsObject physicsObject)
                    {
                        if (CanGrip(physicsObject))
                        {
                            if (gripMode == SuctionGripperGripMode.FixedJoint)
                            {
                                app.LogMessage("Warning", "Core visuals cannot be gripped/released using fixed joints. Falling back to using reparenting.", Visual);
                            }

                            physicsObject.Kinematic = true;
                            physicsObject.Reparent(gripperBodyVisual);

                            GrippedVisuals.Add(physicsObject);
                            RaisePropertyChanged(nameof(GrippedVisuals));

                            UpdateIsGripping();
                            return true;
                        }
                    }
                    else
                    {
                        var rigidBodyAspect = visual.FindAspect<RigidBodyAspect>();
                        if (CanGrip(rigidBodyAspect))
                        {
                            switch (gripMode)
                            {
                                case SuctionGripperGripMode.Reparenting:
                                    rigidBodyAspect.IsEnabled = false;
                                    visual.Reparent(gripperBodyVisual);
                                    break;
                                case SuctionGripperGripMode.FixedJoint:
                                    {
                                        var fixedJointAspect = new DynamicFixedJointAspect();
                                        fixedJointAspect.OtherVisual = gripperBodyVisual;
                                        visual.AddAspect(fixedJointAspect);
                                    }
                                    break;
                            }

                            GrippedVisuals.Add(visual);
                            RaisePropertyChanged(nameof(GrippedVisuals));

                            UpdateIsGripping();
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void ReleaseAll()
        {
            for (int i = GrippedVisuals.Count - 1; i >= 0; --i)
            {
                var visualReference = GrippedVisuals[i];
                if (visualReference != null)
                {
                    Release(visualReference.Visual);
                }
            }

            GrippedVisuals.Clear();
        }

        private void ReleaseHeavy()
        {
            for (int i = GrippedVisuals.Count - 1; i >= 0; --i)
            {
                var visualReference = GrippedVisuals[i];
                if (visualReference != null)
                {
                    var visual = visualReference.Visual;
                    if (visual != null)
                    {
                        var rigidBody = visual.FindAspect<RigidBodyAspect>();
                        if (rigidBody == null || rigidBody.Kinematic || (MassLimitEnabled && rigidBody.Mass > MassLimit))
                        {
                            Release(visual);
                        }
                    }
                }
            }
        }

        private void Release(Visual visual)
        {
            if (visual != null)
            {
                if (GrippedVisuals.Remove(visual))
                {
                    RaisePropertyChanged(nameof(GrippedVisuals));

                    if (visual is PhysicsObject physicsObject)
                    {
                        if (gripMode == SuctionGripperGripMode.FixedJoint)
                        {
                            app.LogMessage("Warning", "Core visuals cannot be gripped/released using fixed joints. Falling back to using reparenting.", Visual);
                        }

                        visual.Reparent(visual.Document.Scene);
                        physicsObject.Kinematic = false;
                    }
                    else
                    {
                        var rigidBody = visual.FindAspect<RigidBodyAspect>();
                        if (rigidBody != null)
                        {
                            switch (gripMode)
                            {
                                case SuctionGripperGripMode.Reparenting:
                                    visual.Reparent(visual.Document.Scene);
                                    rigidBody.IsEnabled = true;
                                    break;
                                case SuctionGripperGripMode.FixedJoint:
                                    foreach (var fixedJoint in new List<DynamicFixedJointAspect>(visual.FindAspects<DynamicFixedJointAspect>()))
                                    {
                                        if (fixedJoint.OtherVisual == gripperBodyVisual)
                                        {
                                            visual.RemoveAspect(fixedJoint);
                                            visual.WakeupPhysics();
                                        }
                                    }
                                    break;
                            }
                        }
                    }

                    UpdateIsGripping();
                }
            }
        }

        private void UpdateIsGripping()
        {
            IsGripping = GrippedVisuals.Count > 0;
        }

        private void CreateBindings()
        {
            if (Visual == null) { return; }

            StateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(State)));
            MassLimitBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(MassLimit)));
            IsGrippingBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(IsGripping)));
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
                case SuctionGripperControlMode.OnOff:
                    StateBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
            }

            if (MassLimitEnabled)
            {
                MassLimitBindableItem.IsBindingInterface = TriStateYNM.Yes;
            }

            if ((Outputs & SuctionGripperOutputs.IsGripping) != SuctionGripperOutputs.None)
            {
                IsGrippingBindableItem.IsBindingInterface = TriStateYNM.Yes;
            }

            UpdateBindingAPI();
        }

        private void OnIsGrippingBindableItemChanged(BindableItem item)
        {
            IsGripping = item.ValueAs<bool>();
        }
    }
}
