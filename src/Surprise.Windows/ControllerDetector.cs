using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Surprise.Core;
using Windows.Gaming.Input;

namespace Surprise.Windows;

public sealed class ControllerDetector : IDisposable
{
    public event Action? Changed;
    private bool disposed;
    public ControllerDetector()
    {
        try
        {
            RawGameController.RawGameControllerAdded += OnChanged;
            RawGameController.RawGameControllerRemoved += OnChanged;
        }
        catch (Exception) { /* Snapshot still provides the XInput fallback and diagnostic. */ }
    }
    private void OnChanged(object? sender, RawGameController controller) { if (!disposed) Changed?.Invoke(); }

    public ControllerSnapshot Snapshot()
    {
        var controllers = new List<ControllerIdentity>();
        string? warning = null;
        int projectedXInputCount = 0;
        try
        {
            foreach (var controller in RawGameController.RawGameControllers)
            {
                string id = controller.NonRoamableId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                controllers.Add(new("wgi:" + id, string.IsNullOrWhiteSpace(controller.DisplayName) ? "Game controller" : controller.DisplayName,
                    "WGI", controller.HardwareVendorId, controller.HardwareProductId));
                if (Gamepad.FromGameController(controller) is not null) projectedXInputCount++;
            }
        }
        catch (Exception ex) { warning = "Windows Gaming Input unavailable: " + ex.Message; }

        // XInput has no physical identity. Represent only the extra connected count to avoid
        // firing twice for controllers exposed through both WGI and XInput.
        int xinputCount = 0;
        for (uint slot = 0; slot < 4; slot++)
        {
            try { if (XInputGetState(slot, out _) == 0) xinputCount++; }
            catch (DllNotFoundException) { break; }
        }
        for (int i = 0; i < Math.Max(0, xinputCount - projectedXInputCount); i++)
            controllers.Add(new("xinput-fallback:" + i, "Connected XInput controller (fallback " + (i + 1) + ")", "XInput"));
        return new(controllers.DistinctBy(c => c.Id).OrderBy(c => c.Id, StringComparer.Ordinal).ToList(), warning);
    }

    [StructLayout(LayoutKind.Sequential)] private struct XInputState
    {
        public uint Packet; public ushort Buttons; public byte LeftTrigger; public byte RightTrigger;
        public short LX, LY, RX, RY;
    }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XInputState state);
    public void Dispose()
    {
        disposed = true;
        try { RawGameController.RawGameControllerAdded -= OnChanged; RawGameController.RawGameControllerRemoved -= OnChanged; }
        catch (Exception) { }
    }
}
