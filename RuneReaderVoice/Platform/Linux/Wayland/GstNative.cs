// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

#if LINUX
// GstNative.cs (RuneReaderVoice)
// GStreamer P/Invoke bindings for RuneReader Voice.
// Extends the screen-capture bindings from RuneReader with audio bus polling support.
// This is a standalone copy — RuneReaderVoice does not share a binary with RuneReader.

using System.Runtime.InteropServices;

namespace RuneReaderVoice.Platform.Linux.Wayland;

internal static class GstNative
{
    private const string GstLib    = "libgstreamer-1.0.so.0";
    private const string GstAppLib = "libgstapp-1.0.so.0";
    private const string GLibLib   = "libglib-2.0.so.0";

    public const int GST_MAP_READ = 1;

    public enum GstState
    {
        GST_STATE_VOID_PENDING = 0,
        GST_STATE_NULL         = 1,
        GST_STATE_READY        = 2,
        GST_STATE_PAUSED       = 3,
        GST_STATE_PLAYING      = 4,
    }

    public enum GstStateChangeReturn
    {
        GST_STATE_CHANGE_FAILURE   = 0,
        GST_STATE_CHANGE_SUCCESS   = 1,
        GST_STATE_CHANGE_ASYNC     = 2,
        GST_STATE_CHANGE_NO_PREROLL = 3,
    }

    public enum GstMessageType : uint
    {
        GST_MESSAGE_UNKNOWN   = 0,
        GST_MESSAGE_EOS       = 1 << 0,
        GST_MESSAGE_ERROR     = 1 << 1,
        GST_MESSAGE_WARNING   = 1 << 2,
        GST_MESSAGE_STATE_CHANGED = 1 << 4,
        GST_MESSAGE_ANY       = uint.MaxValue,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GstMapInfo
    {
        public ulong memory;
        public ulong flags;
        public nint  data;
        public ulong size;
        public nint  user_data0;
        public nint  user_data1;
        public nint  user_data2;
        public nint  user_data3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint domain;
        public int  code;
        public nint message;
    }

    // Core
    [DllImport(GstLib)] public static extern void gst_init(nint argc, nint argv);

    [DllImport(GstLib)]
    public static extern nint gst_parse_launch(
        [MarshalAs(UnmanagedType.LPStr)] string pipeline_description, out nint error);

    [DllImport(GstLib)]
    public static extern GstStateChangeReturn gst_element_set_state(nint element, GstState state);

    [DllImport(GstLib)] public static extern void gst_object_unref(nint obj);

    [DllImport(GstLib)]
    public static extern nint gst_bin_get_by_name(nint bin,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    // Bus — needed for EOS/ERROR detection in audio playback
    [DllImport(GstLib)] public static extern nint gst_element_get_bus(nint element);

    [DllImport(GstLib)]
    public static extern nint gst_bus_timed_pop_filtered(
        nint bus, ulong timeout, GstMessageType types);

    [DllImport(GstLib)] public static extern GstMessageType gst_message_get_type(nint message);
    [DllImport(GstLib)] public static extern void gst_message_unref(nint message);
    [DllImport(GstLib)] public static extern void gst_object_unref_bus(nint bus);

    // Sample/caps/buffer (for screen capture appsink — kept for completeness)
    [DllImport(GstLib)] public static extern nint gst_sample_get_buffer(nint sample);
    [DllImport(GstLib)] public static extern nint gst_sample_get_caps(nint sample);
    [DllImport(GstLib)] public static extern void gst_sample_unref(nint sample);
    [DllImport(GstLib)] public static extern nint gst_caps_get_structure(nint caps, uint index);

    [DllImport(GstLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool gst_structure_get_int(nint structure,
        [MarshalAs(UnmanagedType.LPStr)] string fieldname, out int value);

    [DllImport(GstLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool gst_buffer_map(nint buffer, out GstMapInfo info, int flags);

    [DllImport(GstLib)] public static extern void gst_buffer_unmap(nint buffer, ref GstMapInfo info);

    [DllImport(GstAppLib)]
    public static extern nint gst_app_sink_try_pull_sample(nint appsink, ulong timeout);

    // Error helper
    public static string GErrorToStringAndFree(nint gerrorPtr)
    {
        if (gerrorPtr == nint.Zero) return string.Empty;
        try
        {
            var err = Marshal.PtrToStructure<GError>(gerrorPtr);
            return err.message != nint.Zero
                ? Marshal.PtrToStringUTF8(err.message) ?? "GError"
                : "GError";
        }
        finally { g_error_free(gerrorPtr); }
    }

    [DllImport(GLibLib)] private static extern void g_error_free(nint error);

    public const ulong GST_CLOCK_TIME_NONE = ulong.MaxValue;
    public const ulong GST_SECOND = 1_000_000_000UL;
}
#endif