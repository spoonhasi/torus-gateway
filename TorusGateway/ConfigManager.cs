using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TorusGateway
{
    // config.ini (Win32 INI) 읽기/쓰기 헬퍼. 베이스 디렉토리는 AppDomain.CurrentDomain.BaseDirectory로 고정.
    public static class ConfigManager
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
#pragma warning disable SYSLIB1054
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
#pragma warning restore SYSLIB1054

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string? def, StringBuilder retVal, int size, string filePath);

        // 값이 없으면 defaultValue를 기록한 뒤 반환.
        public static string Read(string fileName, string section, string key, string defaultValue = "")
        {
            StringBuilder buf = new(255);
            _ = GetPrivateProfileString(section, key, null, buf, 255, ResolvePath(fileName));
            if (buf.Length == 0)
            {
                Write(fileName, section, key, defaultValue);
                return defaultValue;
            }
            return buf.ToString();
        }

        public static void Write(string fileName, string section, string key, string contents)
        {
            WritePrivateProfileString(section, key, contents, ResolvePath(fileName));
        }

        private static string ResolvePath(string fileName) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
    }
}
