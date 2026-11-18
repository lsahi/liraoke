using System.Runtime.InteropServices;
using System.Text;
using KaraokeFilmMaker.Application.Practice;
using NAudio.CoreAudioApi;

namespace KaraokeFilmMaker.Infrastructure.Audio;

internal static class PracticeEndpointIdentity
{
    private static readonly PropertyKey ContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly Guid ComputerContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    public static PracticeAudioDevice Read(MMDevice device)
    {
        var properties = device.Properties;
        Guid? container = properties.TryGetValue<Guid>(ContainerKey, out var id) &&
            id != Guid.Empty && id != ComputerContainer ? id : null;
        properties.TryGetValue<string>(PropertyKeys.PKEY_DeviceInterface_FriendlyName, out var adapter);
        uint? form = properties.TryGetValue<uint>(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out var value) ? value : null;
        return new(device.ID, device.FriendlyName, container, ParentOf(device.ID), adapter ?? "", form);
    }

    private static string? ParentOf(string endpoint)
    {
        if (CM_Locate_DevNodeW(out var node, @"SWD\MMDEVAPI\" + endpoint, 0) != 0 ||
            CM_Get_Parent(out var parent, node, 0) != 0 ||
            CM_Get_Device_ID_Size(out var length, parent, 0) != 0) return null;
        var buffer = new StringBuilder(checked((int)length + 1));
        if (CM_Get_Device_IDW(parent, buffer, buffer.Capacity, 0) != 0) return null;
        string id = buffer.ToString();
        return id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith(@"INTELAUDIO\", StringComparison.OrdinalIgnoreCase) ? id : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_Size(out uint size, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);
}
