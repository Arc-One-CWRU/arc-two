using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.Xml.Serialization;

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

    public enum LimitSwitchControlMode
    {
        None,
        LimitSensors
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motors_Category))]
    [HelpUrl("limit_switch")]
    [Obsolete]
    public class LimitSwitch : ExportableVisualAspect, IBindableItemOwner
    {
        private LimitSwitchControlMode controlMode = LimitSwitchControlMode.None;
        private IMotor motor;

        private bool lowerLimitEnabled = false;
        private double lowerLimit = 0;
        private bool switchAtLowerLimit = true;
        private bool atLowerLimit = false;
        private bool upperLimitEnabled = false;
        private double upperLimit = 0;
        private bool switchAtUpperLimit = true;
        private bool atUpperLimit = false;        
        
        private object lowerLimitNotifier;
        private object cancelLowerLimitNotifier;
        private object upperLimitNotifier;
        private object cancelUpperLimitNotifier;

        private BindableItem atLowerLimitBindableItem;
        private BindableItem atUpperLimitBindableItem;

        private const double Epsilon = 1e-3;

        [Required]
        public IMotor Motor
        {
            get { return motor; }
            set
            {
                var oldMotor = motor;
                if (SetProperty(ref motor, value))
                {
                    if (Visual?.Document?.Time > 0)
                    {
                        UpdateAtLimits();
                        RemoveNotifiers(oldMotor);
                        AddNotifiers(motor);
                    }

                    RaisePropertyChanged(nameof(Units));
                }
            }
        }

        [AspectProperty]
        [DefaultValue(LimitSwitchControlMode.None)]
        public LimitSwitchControlMode ControlMode
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

        [AspectProperty(IsVisible = false)]
        [Browsable(false)]
        public UnitsAttribute Units
        {
            get
            {
                if (motor != null)
                {
                    var property = motor.GetType().GetProperty(nameof(motor.DistanceTravelled));
                    var attributes = property.GetCustomAttributes(true);
                    foreach (var attribute in attributes)
                    {
                        if (attribute is UnitsAttribute unitsAttribute)
                        {
                            return unitsAttribute;
                        }
                    }
                }

                return null;
            }
        }

        [AspectProperty]
        [DefaultValue(false)]
        [Cat("Lower Limit")]
        public bool LowerLimitEnabled
        {
            get { return lowerLimitEnabled; }
            set
            {
                if (SetProperty(ref lowerLimitEnabled, value))
                {
                    UpdateNotifiers();
                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(IsVisiblePropertyLink = nameof(LowerLimitEnabled), IsEnabledPropertyLink = nameof(LowerLimitEnabled), UnitsAttributePropertyLink = nameof(Units))]
        [DefaultValue(0.0)]
        [Cat("Lower Limit")]
        public double LowerLimit
        {
            get { return lowerLimit; }
            set
            {
                if (SetProperty(ref lowerLimit, value))
                {
                    UpdateNotifiers();
                    UpdateAtLimits();
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(LowerLimitEnabled), IsEnabledPropertyLink = nameof(LowerLimitEnabled))]
        [DefaultValue(true)]
        [Cat("Lower Limit")]
        public bool SwitchAtLowerLimit
        {
            get { return switchAtLowerLimit; }
            set { SetProperty(ref switchAtLowerLimit, value); }
        }

        [AspectProperty]
        [DefaultValue(false)]
        [Cat("Upper Limit")]
        public bool UpperLimitEnabled
        {
            get { return upperLimitEnabled; }
            set
            {
                if (SetProperty(ref upperLimitEnabled, value))
                {
                    UpdateNotifiers();
                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(IsVisiblePropertyLink = nameof(UpperLimitEnabled), IsEnabledPropertyLink = nameof(UpperLimitEnabled), UnitsAttributePropertyLink = nameof(Units))]
        [DefaultValue(0.0)]
        [Cat("Upper Limit")]
        public double UpperLimit
        {
            get { return upperLimit; }
            set
            {
                if (SetProperty(ref upperLimit, value))
                {
                    UpdateNotifiers();
                    UpdateAtLimits();
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(UpperLimitEnabled), IsEnabledPropertyLink = nameof(UpperLimitEnabled))]
        [DefaultValue(true)]
        [Cat("Upper Limit")]
        public bool SwitchAtUpperLimit
        {
            get { return switchAtUpperLimit; }
            set { SetProperty(ref switchAtUpperLimit, value); }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(LowerLimitEnabled), IsEnabledPropertyLink = nameof(LowerLimitEnabled))]
        [Cat("Limit Sensors")]
        [XmlIgnore]
        public bool AtLowerLimit
        {
            get { return atLowerLimit; }
            private set { SetProperty(ref atLowerLimit, value); }
        }

        [AspectEditor(IsVisiblePropertyLink = nameof(UpperLimitEnabled), IsEnabledPropertyLink = nameof(UpperLimitEnabled))]
        [AspectProperty]
        [Cat("Limit Sensors")]
        [XmlIgnore]
        public bool AtUpperLimit
        {
            get { return atUpperLimit; }
            private set { SetProperty(ref atUpperLimit, value); }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem AtLowerLimitBindableItem
        {
            get { return atLowerLimitBindableItem; }
            private set
            {
                atLowerLimitBindableItem = value;
                atLowerLimitBindableItem.Value = AtLowerLimit;
                atLowerLimitBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem AtUpperLimitBindableItem
        {
            get { return atUpperLimitBindableItem; }
            private set
            {
                atUpperLimitBindableItem = value;
                atUpperLimitBindableItem.Value = AtUpperLimit;
                atUpperLimitBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems
        {
            get
            {
                if (AtLowerLimitBindableItem != null) yield return AtLowerLimitBindableItem;
                if (AtUpperLimitBindableItem != null) yield return AtUpperLimitBindableItem;
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

            UpdateAtLimits();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            UpdateNotifiers();
            UpdateAtLimits();
        }

        protected override void OnReset()
        {
            base.OnReset();

            RemoveNotifiers();
            UpdateAtLimits();
        }

        protected override void OnRemoved()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem?.DetachFromVisual();
            }

            RemoveNotifiers();

            base.OnRemoved();
        }

        private void UpdateAtLimits()
        {
            UpdateAtLimits((Motor != null) ? Motor.DistanceTravelled : 0);
        }

        private void UpdateAtLimits(double value)
        {
            AtLowerLimit = (LowerLimitEnabled) ? Util.LE(value, LowerLimit) : false;
            AtUpperLimit = (UpperLimitEnabled) ? Util.GE(value, UpperLimit) : false;

            if (AtLowerLimitBindableItem != null) { AtLowerLimitBindableItem.Value = AtLowerLimit; }
            if (AtUpperLimitBindableItem != null) { AtUpperLimitBindableItem.Value = AtUpperLimit; }

            Switch();
        }

        private void Switch()
        {
            if (Motor != null)
            {
                if (UpperLimitEnabled && SwitchAtUpperLimit && Util.GE(motor.DistanceTravelled, UpperLimit) && Motor.Direction == MotorDirection.Forwards) { Motor.State = MotorState.Off; }
                if (LowerLimitEnabled && SwitchAtLowerLimit && Util.LE(motor.DistanceTravelled, LowerLimit) && Motor.Direction == MotorDirection.Reverse) { Motor.State = MotorState.Off; }
            }
        }

        private void UpdateNotifiers()
        {
            RemoveNotifiers(motor);
            AddNotifiers(motor);
        }

        private void AddNotifiers()
        {
            AddNotifiers(Motor);
        }

        private void AddNotifiers(IMotor motor)
        {
            if (motor != null && IsInitialized)
            {
                // If we have equal lower and upper limits then the motor will always be at or
                // violating the limits. The notifiers below will be triggered immediately,
                // causing infinite recursion.
                if (LowerLimitEnabled == false || UpperLimitEnabled == false || Util.LT(LowerLimit, UpperLimit))
                {
                    if (LowerLimitEnabled)
                    {
                        if (Util.GT(motor.DistanceTravelled, LowerLimit))
                        {
                            lowerLimitNotifier = motor.AddDistanceNotifier(LowerLimit, null);
                        }
                        else
                        {
                            var cancelLowerLimitValue = (UpperLimitEnabled) ? Math.Min(LowerLimit + Epsilon, UpperLimit) : LowerLimit + Epsilon;
                            cancelLowerLimitNotifier = motor.AddDistanceNotifier(cancelLowerLimitValue, null);
                        }
                    }

                    if (UpperLimitEnabled)
                    {
                        if (Util.LT(motor.DistanceTravelled, UpperLimit))
                        {
                            upperLimitNotifier = motor.AddDistanceNotifier(UpperLimit, null);
                        }
                        else
                        {
                            var cancelUpperLimitValue = (LowerLimitEnabled) ? Math.Max(UpperLimit - Epsilon, LowerLimit) : UpperLimit - Epsilon;
                            cancelUpperLimitNotifier = motor.AddDistanceNotifier(cancelUpperLimitValue, null);
                        }
                    }

                    if (LowerLimitEnabled || UpperLimitEnabled)
                    {
                        motor.OnNotifyDistanceListeners -= MotorAspect_OnNotifyDistance;
                        motor.OnNotifyDistanceListeners += MotorAspect_OnNotifyDistance;
                    }
                }
                else
                {
                    UpdateAtLimits();
                }

                motor.StateListeners -= MotorAspect_OnStateChanged;
                motor.StateListeners += MotorAspect_OnStateChanged;
            }
        }

        private void RemoveNotifiers()
        {
            RemoveNotifiers(Motor);
        }

        private void RemoveNotifiers(IMotor motor)
        {
            if (motor != null)
            {
                motor.OnNotifyDistanceListeners -= MotorAspect_OnNotifyDistance;

                if (lowerLimitNotifier != null)
                {
                    motor.RemoveDistanceNotifier(lowerLimitNotifier);
                }

                if (cancelLowerLimitNotifier != null)
                {
                    motor.RemoveDistanceNotifier(cancelLowerLimitNotifier);
                }

                if (upperLimitNotifier != null)
                {
                    motor.RemoveDistanceNotifier(upperLimitNotifier);
                }

                if (cancelUpperLimitNotifier != null)
                {
                    motor.RemoveDistanceNotifier(cancelUpperLimitNotifier);
                }
            }
        }

        private void MotorAspect_OnNotifyDistance(Visual sender, NotifyDistanceInfo info)
        {
            UpdateAtLimits(info.Distance);
            UpdateNotifiers();
        }

        private void MotorAspect_OnStateChanged(object sender, MotorState oldState, MotorState newState)
        {
            if (Motor == null) { return; }

            if (newState == MotorState.On)
            {
                Switch();
            }
        }

        private void CreateBindings()
        {
            if (Visual == null) { return; }

            AtLowerLimitBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtLowerLimit)));
            AtUpperLimitBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtUpperLimit)));
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
                case LimitSwitchControlMode.LimitSensors:
                    if (LowerLimitEnabled) { AtLowerLimitBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    if (UpperLimitEnabled) { AtUpperLimitBindableItem.IsBindingInterface = TriStateYNM.Yes; }
                    break;
            }

            UpdateBindingAPI();
        }
    }
}
