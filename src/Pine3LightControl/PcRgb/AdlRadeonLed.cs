using System;
using System.Runtime.InteropServices;
using Pine3LightControl.Core;

namespace Pine3LightControl.PcRgb;

/// <summary>
/// Gravação da cor do logótipo Radeon na flash do controlador, pela API oficial da AMD
/// (ADL, atiadlxx.dll instalada com o driver; radeonusbled.h do ADL SDK):
/// ADL2_Adapter_Radeon_Usb_Led_Flash_Save — "save programmed LED configuration onto LED
/// flash to persist on reboot and power down". A cor é programada antes pelo HID (como
/// sempre); aqui só se pede ao driver que a grave. Só a pedido do utilizador.
/// </summary>
internal static class AdlRadeonLed
{
    private const int AdlOk = 0;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr MallocCallback(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MainControlCreate(MallocCallback callback, int enumConnectedAdapters, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MainControlDestroy(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NumberOfAdaptersGet(IntPtr context, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UsbLedSupportGet(IntPtr context, int adapterIndex, [MarshalAs(UnmanagedType.U1)] out bool supported);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UsbLedFlashSave(IntPtr context, int adapterIndex);

    // Mantido vivo enquanto houver contexto (o ADL chama-o para reservar memória)
    private static readonly MallocCallback Malloc = size => Marshal.AllocHGlobal(size);

    /// <summary>Grava na flash do LED. Devolve (sucesso, mensagem para a UI).</summary>
    public static (bool Ok, string Message) FlashSave()
    {
        if (!NativeLibrary.TryLoad("atiadlxx.dll", out var lib))
            return (false, "AMD driver library (atiadlxx.dll) not found — is the AMD graphics driver installed?");
        IntPtr ctx = IntPtr.Zero;
        MainControlDestroy? destroy = null;
        try
        {
            var create = Get<MainControlCreate>(lib, "ADL2_Main_Control_Create");
            destroy = Get<MainControlDestroy>(lib, "ADL2_Main_Control_Destroy");
            var count = Get<NumberOfAdaptersGet>(lib, "ADL2_Adapter_NumberOfAdapters_Get");
            var support = Get<UsbLedSupportGet>(lib, "ADL2_Adapter_Radeon_USB_LED_Support_Get");
            var save = Get<UsbLedFlashSave>(lib, "ADL2_Adapter_Radeon_Usb_Led_Flash_Save");
            if (create is null || destroy is null || count is null || support is null || save is null)
                return (false, "This AMD driver doesn't offer the Radeon LED save function — update the driver.");

            int r = create(Malloc, 1, out ctx);
            if (r != AdlOk) return (false, $"Couldn't start the AMD driver library (ADL error {r}).");
            r = count(ctx, out int n);
            if (r != AdlOk) return (false, $"Couldn't list the graphics adapters (ADL error {r}).");

            for (int i = 0; i < n; i++)
            {
                if (support(ctx, i, out bool led) != AdlOk || !led) continue;
                r = save(ctx, i);
                Log.Write($"[GPU] ADL Flash_Save no adaptador {i}: {r}");
                return r == AdlOk
                    ? (true, "Saved in the graphics card.")
                    : (false, $"The driver refused to save (ADL error {r}). Close the AMD RGB LED tool and try again.");
            }
            Log.Write($"[GPU] ADL: nenhum dos {n} adaptador(es) indica LED USB");
            return (false, "The AMD driver doesn't report a Radeon LED on this card.");
        }
        catch (Exception e)
        {
            Log.Write($"[GPU] ADL: {e.Message}");
            return (false, "Error: " + e.Message);
        }
        finally
        {
            if (ctx != IntPtr.Zero) try { destroy?.Invoke(ctx); } catch { }
            NativeLibrary.Free(lib);
        }
    }

    private static T? Get<T>(IntPtr lib, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
}
