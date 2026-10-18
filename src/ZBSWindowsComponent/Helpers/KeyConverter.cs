using System;
using System.Collections.Generic;
using System.Linq;

namespace ZBSWindowsComponent.Helpers
{
    /// <summary>
    /// Конвертация строковых представлений клавиш в VK коды и модификаторы
    /// </summary>
    internal static class KeyConverter
    {
        /// <summary>
        /// Парсинг строки клавиши (например "Ctrl+Shift+F13")
        /// </summary>
        /// <param name="keyString">Строка клавиши</param>
        /// <returns>Кортеж (модификаторы, VK код)</returns>
        public static (uint modifiers, uint vkCode) Parse(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
            {
                throw new ArgumentException("Строка клавиши не может быть пустой");
            }

            uint modifiers = 0;
            uint vkCode = 0;

            // Разделить по знаку '+'
            var parts = keyString.Split('+')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (parts.Length == 0)
            {
                throw new ArgumentException("Некорректная строка клавиши");
            }

            // Обработать каждую часть
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                bool isLastPart = (i == parts.Length - 1);

                // Попробовать распознать как модификатор
                if (TryParseModifier(part, out uint modifier))
                {
                    if (isLastPart)
                    {
                        throw new ArgumentException($"Модификатор '{part}' не может быть последним в комбинации");
                    }
                    modifiers |= modifier;
                }
                // Иначе это основная клавиша
                else
                {
                    if (!isLastPart)
                    {
                        throw new ArgumentException($"Основная клавиша '{part}' должна быть последней в комбинации");
                    }
                    vkCode = ParseVirtualKey(part);
                }
            }

            if (vkCode == 0)
            {
                throw new ArgumentException("Не указана основная клавиша");
            }

            // Добавить MOD_NOREPEAT для предотвращения повторных срабатываний
            modifiers |= NativeMethods.MOD_NOREPEAT;

            return (modifiers, vkCode);
        }

        /// <summary>
        /// Попытка распознать модификатор
        /// </summary>
        private static bool TryParseModifier(string modifier, out uint value)
        {
            switch (modifier.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    value = NativeMethods.MOD_CONTROL;
                    return true;

                case "ALT":
                    value = NativeMethods.MOD_ALT;
                    return true;

                case "SHIFT":
                    value = NativeMethods.MOD_SHIFT;
                    return true;

                case "WIN":
                case "WINDOWS":
                    value = NativeMethods.MOD_WIN;
                    return true;

                default:
                    value = 0;
                    return false;
            }
        }

        /// <summary>
        /// Парсинг основной клавиши в VK код
        /// </summary>
        private static uint ParseVirtualKey(string key)
        {
            string upperKey = key.ToUpperInvariant();

            // Функциональные клавиши F1-F24
            if (upperKey.StartsWith("F")
                && upperKey.Length > 1
                && int.TryParse(upperKey.Substring(1), out int fNumber)
                && fNumber >= 1
                && fNumber <= 24)
            {
                return (uint)(0x70 + fNumber - 1); // VK_F1 = 0x70
            }

            // Буквы A-Z
            if (upperKey.Length == 1 && char.IsLetter(upperKey[0]))
            {
                return (uint)upperKey[0]; // VK коды для A-Z совпадают с ASCII
            }

            // Цифры 0-9
            if (upperKey.Length == 1 && char.IsDigit(upperKey[0]))
            {
                return (uint)upperKey[0]; // VK коды для 0-9 совпадают с ASCII
            }

            // Специальные клавиши
            if (_specialKeys.TryGetValue(upperKey, out uint vk))
            {
                return vk;
            }

            throw new ArgumentException($"Неизвестная клавиша: {key}");
        }

        /// <summary>
        /// Словарь специальных клавиш
        /// </summary>
        private static readonly Dictionary<string, uint> _specialKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            // Клавиши управления
            { "BACKSPACE", 0x08 },
            { "BACK", 0x08 },
            { "TAB", 0x09 },
            { "ENTER", 0x0D },
            { "RETURN", 0x0D },
            { "PAUSE", 0x13 },
            { "CAPSLOCK", 0x14 },
            { "CAPS", 0x14 },
            { "ESCAPE", 0x1B },
            { "ESC", 0x1B },
            { "SPACE", 0x20 },
            { "PAGEUP", 0x21 },
            { "PGUP", 0x21 },
            { "PAGEDOWN", 0x22 },
            { "PGDN", 0x22 },
            { "END", 0x23 },
            { "HOME", 0x24 },
            { "LEFT", 0x25 },
            { "UP", 0x26 },
            { "RIGHT", 0x27 },
            { "DOWN", 0x28 },
            { "PRINTSCREEN", 0x2C },
            { "PRINT", 0x2C },
            { "INSERT", 0x2D },
            { "INS", 0x2D },
            { "DELETE", 0x2E },
            { "DEL", 0x2E },

            // Numpad
            { "NUMPAD0", 0x60 },
            { "NUMPAD1", 0x61 },
            { "NUMPAD2", 0x62 },
            { "NUMPAD3", 0x63 },
            { "NUMPAD4", 0x64 },
            { "NUMPAD5", 0x65 },
            { "NUMPAD6", 0x66 },
            { "NUMPAD7", 0x67 },
            { "NUMPAD8", 0x68 },
            { "NUMPAD9", 0x69 },
            { "MULTIPLY", 0x6A },
            { "ADD", 0x6B },
            { "SUBTRACT", 0x6D },
            { "DECIMAL", 0x6E },
            { "DIVIDE", 0x6F },

            // Символы
            { "SEMICOLON", 0xBA },
            { "EQUAL", 0xBB },
            { "COMMA", 0xBC },
            { "MINUS", 0xBD },
            { "PERIOD", 0xBE },
            { "SLASH", 0xBF },
            { "TILDE", 0xC0 },
            { "OPENBRACKET", 0xDB },
            { "BACKSLASH", 0xDC },
            { "CLOSEBRACKET", 0xDD },
            { "QUOTE", 0xDE },

            // Медиа клавиши
            { "VOLUMEMUTE", 0xAD },
            { "VOLUMEDOWN", 0xAE },
            { "VOLUMEUP", 0xAF },
            { "MEDIANEXTTRACK", 0xB0 },
            { "MEDIAPREVTRACK", 0xB1 },
            { "MEDIASTOP", 0xB2 },
            { "MEDIAPLAYPAUSE", 0xB3 }
        };

        /// <summary>
        /// Получить описание клавиши для отладки
        /// </summary>
        public static string GetKeyDescription(uint modifiers, uint vkCode)
        {
            var parts = new List<string>();

            // Модификаторы
            if ((modifiers & NativeMethods.MOD_CONTROL) != 0)
                parts.Add("Ctrl");
            if ((modifiers & NativeMethods.MOD_ALT) != 0)
                parts.Add("Alt");
            if ((modifiers & NativeMethods.MOD_SHIFT) != 0)
                parts.Add("Shift");
            if ((modifiers & NativeMethods.MOD_WIN) != 0)
                parts.Add("Win");

            // Основная клавиша
            string keyName = GetKeyName(vkCode);
            parts.Add(keyName);

            return string.Join("+", parts);
        }

        /// <summary>
        /// Получить имя клавиши по VK коду
        /// </summary>
        private static string GetKeyName(uint vkCode)
        {
            // F-клавиши
            if (vkCode >= 0x70 && vkCode <= 0x87)
            {
                return $"F{vkCode - 0x70 + 1}";
            }

            // Буквы A-Z
            if (vkCode >= 0x41 && vkCode <= 0x5A)
            {
                return ((char)vkCode).ToString();
            }

            // Цифры 0-9
            if (vkCode >= 0x30 && vkCode <= 0x39)
            {
                return ((char)vkCode).ToString();
            }

            // Поиск в словаре специальных клавиш
            foreach (var kvp in _specialKeys)
            {
                if (kvp.Value == vkCode)
                {
                    return kvp.Key;
                }
            }

            return $"VK_{vkCode:X2}";
        }
    }
}
