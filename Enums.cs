using System.ComponentModel;

namespace ToolsServer
{
    public static class Enums
    {
        [TypeConverter]
        public enum VideoType : short
        {
            VideoAndAudioHighestQuality,
            VideoAndAudioQuick,
            Audio,
            AudioMp3,
            Video
        }

        [TypeConverter]
        public enum ResizeType : short
        {
            Percentage,
            Dimensions
        }

        [TypeConverter]
        public enum GraphTimePeriod : short
        {
            Week,
            Month,
            Year
        }

        [TypeConverter]
        public enum QrCodeType : short
        {
            LINK,
            TEXT,
            WIFI,
            FILE,
            PROFILE,
            VCARD
        }

        [TypeConverter]
        public enum WifiEncryption : short
        {
            None,
            WPA,
            WEP
        }

        [TypeConverter]
        public enum DocumentType : short
        {
            PDF,
            Word,
            Excel
        }
    }
}
