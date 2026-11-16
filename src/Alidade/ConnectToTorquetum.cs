using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using EtoClipboard = Eto.Forms.Clipboard;

namespace Torquetum.Alidade;

public sealed class ConnectToTorquetum : AlidadeAsyncComponent
{
    private const string PairAction = "Pair with Torquetum";

    private ProjectSession? _session;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a running Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("704efa3e-ae66-48c5-89fc-01ebffe84565");
    protected override Bitmap Icon => ComponentIcons.Load("Connect");
    public override void CreateAttributes() => m_attributes = new ConnectAttributes(this);

    protected override void RegisterInputParams(GH_InputParamManager manager) { }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddTextParameter("Status", "S", "Alidade connection status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var session = _session;
        if (session != null && (document == null || !session.IsValidFor(document.RuntimeID)))
        {
            ResetState();
            session = null;
        }
        if (IsPending || session == null || Failure != null)
        {
            var error = Failure?.Message;
            ReportStatus(data, 1, error ?? (IsPending ? "Connecting…" : "Waiting for pairing"), error);
            return;
        }

        data.SetData(0, new AlidadeProject(session));
        var project = session.Status;
        var access = !project.CanRead ? "unavailable" : project.CanWrite ? "read/write" : "read-only";
        var warning = !project.CanRead || project.AvailabilityReason == "PROJECT_BUSY"
            ? AlidadeClient.ErrorMessage(project.AvailabilityReason ?? "PROJECT_BUSY") : null;
        ReportStatus(data, 1, $"{project.ProjectName} ({access})", warning);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var enabled = CanUse(OnPingDocument()) && !IsPending;
        Menu_AppendItem(menu, PairAction, (_, _) => Pair(), enabled);
        Menu_AppendItem(menu, "Refresh status", (_, _) => Refresh(), enabled && _session?.IsValid == true);
        Menu_AppendItem(menu, "Disconnect", (_, _) => Disconnect(), _session != null || IsPending);
    }

    private void Pair(bool paste = false, string? reason = null)
    {
        var document = OnPingDocument();
        if (IsPending || !CanUse(document))
            return;

        var code = paste ? "" : EtoClipboard.Instance.Text?.Trim() ?? "";
        var fromClipboard = AlidadeClient.HasInvitationPrefix(code);
        if (!fromClipboard)
        {
            const string prompt = "Paste the pairing code from Torquetum Project Settings.";
            if (!Rhino.UI.Dialogs.ShowEditBox(PairAction, reason == null ? prompt : $"{reason}\n\n{prompt}", "", false, out code)
                || !ReferenceEquals(OnPingDocument(), document) || !CanUse(document) || IsPending)
                return;
        }

        ResetState();
        StartRequest(document, null, cancellation => AlidadeClient.PairAsync(code, document.RuntimeID, cancellation),
            session => _session = session,
            error =>
            {
                if (fromClipboard && error.Code == "INVITE_INVALID")
                    Pair(paste: true, reason: error.Message);
            }, abandon: session => session.Disconnect());
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        if (IsPending || !CanUseProject(document, _session))
            return;

        var session = _session;
        StartRequest(document, session, cancellation => AlidadeClient.ReadProjectAsync(session, document.RuntimeID, cancellation),
            status => session.Status = status);
    }

    private void Disconnect()
    {
        CancelRequest();
        ResetState();
        if (OnPingDocument() is { } document)
            RequestSolution(document);
    }

    protected override void ResetState()
    {
        _session?.Disconnect();
        _session = null;
    }

    private sealed class ConnectAttributes : GH_ComponentAttributes
    {
        private readonly ConnectToTorquetum _component;
        private RectangleF _pairButton;

        internal ConnectAttributes(ConnectToTorquetum component) : base(component) => _component = component;

        protected override void Layout()
        {
            base.Layout();
            const float width = 102;
            const float height = 18;
            _pairButton = new RectangleF(ContentBox.Left + (ContentBox.Width - width) / 2,
                Bounds.Top - height - 3, width, height);
            Bounds = RectangleF.Union(Bounds, _pairButton);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;

            using var button = GH_Capsule.CreateTextCapsule(_pairButton, _pairButton, GH_Palette.Blue, PairAction);
            button.Render(graphics, Selected, _component.IsPending || !_component.CanUse(_component.OnPingDocument()), false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Left || !_pairButton.Contains(e.CanvasLocation))
                return base.RespondToMouseDown(sender, e);

            _component.Pair();
            return GH_ObjectResponse.Handled;
        }
    }
}
