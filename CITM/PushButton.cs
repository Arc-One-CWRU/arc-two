using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;

using Microsoft.DirectX;

using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.PLC;

namespace Demo3D.Components {
    
    using Properties;

    public enum PushButtonControlMode
    {
        None = 0,
        OnOff = 1,
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.ControlPanels_Category))]
    [HelpUrl("push_button")]
    public class PushButton : ExportableVisualAspect
    {
        private PushButtonControlMode controlMode = PushButtonControlMode.None;
        private VisualNormal pushAxis = null;
        private double pushDistance = 0.005;
        private bool manualRelease = false;

        [Required]
        public VisualNormal PushAxis
        {
            get { return pushAxis; }
            set { SetProperty(ref pushAxis, value); }
        }

        [Distance]
        [DefaultValue(0.005)]
        public double PushDistance
        {
            get { return pushDistance; }
            set { SetProperty(ref pushDistance, value); }
        }

        [DefaultValue(false)]
        public bool ManualRelease
        {
            get { return manualRelease; }
            set { SetProperty(ref manualRelease, value); }
        }

        [DefaultValue(PushButtonControlMode.None)]
        public PushButtonControlMode ControlMode {
            get { return controlMode; }
            set
            {
                if (SetProperty(ref controlMode, value))
                {
                    UpdateBindingInterface();
                }
            }
        }

        [XmlIgnore]
        public bool IsPushed
        {
            get { return Pushed.Value; }
            set { Pushed.Value = value; }
        }

        [Auto] readonly WriteToServer<bool> Pushed;

        protected override IEnumerable<BindableItem> BindingInterface
        {
            get { if (controlMode == PushButtonControlMode.OnOff) { yield return Pushed; } }
        }

        protected override void OnAdded()
        {
            Visual.Draggable = false;
            Visual.SelectParentWhenPicked = false;
            Visual.IsStatic = false;
        }

        protected override void OnEnabled()
        {
            Pushed.ValueChangedListeners += Pushed_ValueChangedListeners;

            AddVisualListeners();
        }

        protected override void OnDisabled()
        {
            Pushed.ValueChangedListeners -= Pushed_ValueChangedListeners;

            RemoveVisualListeners();
        }

        void AddVisualListeners()
        {
            RemoveVisualListeners();
            Visual.OnClick.NativeListeners += OnClick_NativeListeners;
            Visual.OnMouseUp.NativeListeners += OnMouseUp_NativeListeners;
        }

        void RemoveVisualListeners()
        {
            Visual.OnClick.NativeListeners -= OnClick_NativeListeners;
            Visual.OnMouseUp.NativeListeners -= OnMouseUp_NativeListeners;
        }

        protected override void OnInitialize()
        {
            AddVisualListeners();
        }

        protected override void OnReset()
        {
            AddVisualListeners();
        }

        void OnClick_NativeListeners(Visual sender, PickInfo arg)
        {
            Pushed.Value = (ManualRelease) ? !Pushed.Value : true;
        }

        void OnMouseUp_NativeListeners(Visual sender, PickInfo arg)
        {
            if (ManualRelease == false)
            {
                Pushed.Value = false;
            }
        }

        private void Pushed_ValueChangedListeners(BindableItem obj)
        {
            if (Pushed)
            {
                var offset = (PushAxis?.WorldNormal ?? Vector3.Zero) * PushDistance;
                Visual.MoveTo(Visual.WorldLocation - offset);
            }
            else
            {
                Visual.MoveToInitialPosition();
            }

            RaisePropertyChanged(nameof(IsPushed));
        }
    }
}
