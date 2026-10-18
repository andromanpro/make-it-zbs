using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

using ScriptEngine.HostedScript.Library;

namespace ZBSWindowsComponent
{
    /// <summary>
    /// Расширенная работа с буфером обмена
    /// </summary>
    [ContextClass("БуферОбменаРасширенный", "ClipboardEx")]
    public class ClipboardEx : AutoContext<ClipboardEx>
    {
        [ScriptConstructor]
        public static ClipboardEx Constructor()
        {
            return new ClipboardEx();
        }

        [ContextMethod("ОчиститьВременныеФайлы", "CleanupTemporaryFiles")]
        public int CleanupTemporaryFiles(int maximumFiles)
        {
            return TemporaryFileStore.Cleanup(maximumFiles);
        }

        // Phase 15 (аудит): System.Windows.Forms.Clipboard требует STA-поток.
        // Поток OneScript часто MTA → Clipboard.* бросал ThreadStateException,
        // который глотался в catch → методы всегда возвращали false/пусто
        // (напр. картинка из буфера «не находилась» → деградация в скриншот).
        // Clipboard OLE objects keep the same STA owner and message pump
        // throughout capture, paste and restoration, including Office formats.
        private static T RunInSta<T>(Func<T> func)
        {
            return ClipboardDispatcher.Shared.Run(func);
        }

        private static void RunInSta(Action action)
        {
            RunInSta(() =>
            {
                action();
                return true;
            });
        }

        /// <summary>
        /// Проверить, содержит ли буфер обмена текст
        /// </summary>
        [ContextMethod("СодержитТекст", "ContainsText")]
        public bool ContainsText()
        {
            try
            {
                return RunInSta(() => Clipboard.ContainsText());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проверить, содержит ли буфер обмена изображение
        /// </summary>
        [ContextMethod("СодержитИзображение", "ContainsImage")]
        public bool ContainsImage()
        {
            try
            {
                return RunInSta(() => Clipboard.ContainsImage());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проверить, содержит ли буфер обмена файлы
        /// </summary>
        [ContextMethod("СодержитФайлы", "ContainsFileDropList")]
        public bool ContainsFileDropList()
        {
            try
            {
                return RunInSta(() => Clipboard.ContainsFileDropList());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Получить текст из буфера обмена
        /// </summary>
        [ContextMethod("ПолучитьТекст", "GetText")]
        public string GetText()
        {
            try
            {
                return RunInSta(() =>
                    Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
            }
            catch
            {
                // Clipboard reads are best effort; callers treat an unavailable clipboard as empty.
            }

            return string.Empty;
        }

        /// <summary>
        /// Получить изображение из буфера обмена и сохранить в файл
        /// </summary>
        [ContextMethod("ПолучитьИзображение", "GetImage")]
        public string GetImage(string outputPath = null)
        {
            try
            {
                // STA-маршалинг (см. RunInSta) + using: раньше Image из GetImage
                // никогда не диспозился → утечка GDI-объекта на каждый вызов.
                using (Image image = RunInSta(() =>
                {
                    using (var source = Clipboard.ContainsImage() ? Clipboard.GetImage() : null)
                        return source == null ? null : new Bitmap(source);
                }))
                {
                    if (image == null)
                    {
                        throw new RuntimeException("Буфер обмена не содержит изображение");
                    }

                    // Определить путь сохранения
                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        outputPath = GenerateImageFileName();
                    }

                    // Убедиться что директория существует
                    string directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Определить формат по расширению
                    ImageFormat format = GetImageFormatFromExtension(outputPath);

                    // Сохранить
                    image.Save(outputPath, format);

                    return outputPath;
                }
            }
            catch (RuntimeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RuntimeException($"Ошибка при получении изображения: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить список файлов из буфера обмена
        /// </summary>
        [ContextMethod("ПолучитьФайлы", "GetFileDropList")]
        public ArrayImpl GetFileDropList()
        {
            var result = new ArrayImpl();

            try
            {
                string[] files = RunInSta(() =>
                {
                    if (!Clipboard.ContainsFileDropList())
                    {
                        return new string[0];
                    }

                    StringCollection source = Clipboard.GetFileDropList();
                    var copy = new string[source.Count];
                    source.CopyTo(copy, 0);
                    return copy;
                });

                foreach (string file in files)
                {
                    result.Add(ValueFactory.Create(file));
                }
            }
            catch
            {
                // Preserve the partial result when Windows withdraws the clipboard during enumeration.
            }

            return result;
        }

        /// <summary>
        /// Установить текст в буфер обмена
        /// </summary>
        [ContextMethod("УстановитьТекст", "SetText")]
        public void SetText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new RuntimeException("Текст не может быть пустым");
            }

            try
            {
                RunInSta(() => Clipboard.SetText(text));
            }
            catch (Exception ex)
            {
                throw new RuntimeException($"Ошибка при установке текста в буфер обмена: {ex.Message}");
            }
        }

        /// <summary>
        /// Установить изображение в буфер обмена из файла
        /// </summary>
        [ContextMethod("УстановитьИзображение", "SetImage")]
        public void SetImage(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new RuntimeException("Путь к изображению не может быть пустым");
            }

            if (!File.Exists(imagePath))
            {
                throw new RuntimeException($"Файл не найден: {imagePath}");
            }

            try
            {
                RunInSta(() =>
                {
                    using (Image image = Image.FromFile(imagePath))
                    {
                        Clipboard.SetImage(image);
                    }
                });
            }
            catch (Exception ex)
            {
                throw new RuntimeException($"Ошибка при установке изображения: {ex.Message}");
            }
        }

        /// <summary>
        /// Очистить буфер обмена
        /// </summary>
        [ContextMethod("Очистить", "Clear")]
        public void Clear()
        {
            try
            {
                RunInSta(() => Clipboard.Clear());
            }
            catch (Exception ex)
            {
                throw new RuntimeException($"Ошибка при очистке буфера обмена: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить информацию о содержимом буфера обмена
        /// </summary>
        [ContextMethod("ПолучитьИнформацию", "GetInfo")]
        public IValue GetInfo()
        {
            var result = new StructureImpl();

            try
            {
                // Все обращения к буферу — одним STA-заходом (см. RunInSta)
                var snapshot = RunInSta(() =>
                {
                    var s = new bool[4];
                    s[0] = Clipboard.ContainsText();
                    s[1] = Clipboard.ContainsImage();
                    s[2] = Clipboard.ContainsFileDropList();
                    s[3] = Clipboard.ContainsAudio();
                    string[] fmts = new string[0];
                    IDataObject d = Clipboard.GetDataObject();
                    if (d != null) { fmts = d.GetFormats(); }
                    return new Tuple<bool[], string[]>(s, fmts);
                });

                result.Insert("имеетТекст", ValueFactory.Create(snapshot.Item1[0]));
                result.Insert("имеетИзображение", ValueFactory.Create(snapshot.Item1[1]));
                result.Insert("имеетФайлы", ValueFactory.Create(snapshot.Item1[2]));
                result.Insert("имеетAudio", ValueFactory.Create(snapshot.Item1[3]));

                // Получить список форматов
                var formats = new ArrayImpl();
                if (snapshot.Item2 != null)
                {
                    string[] formatNames = snapshot.Item2;
                    foreach (string format in formatNames)
                    {
                        formats.Add(ValueFactory.Create(format));
                    }
                }
                result.Insert("форматы", formats);
            }
            catch
            {
                // В случае ошибки вернуть пустую структуру
                result.Insert("имеетТекст", ValueFactory.Create(false));
                result.Insert("имеетИзображение", ValueFactory.Create(false));
                result.Insert("имеетФайлы", ValueFactory.Create(false));
                result.Insert("имеетAudio", ValueFactory.Create(false));
                result.Insert("форматы", new ArrayImpl());
            }

            return result;
        }

        /// <summary>
        /// Проверить наличие конкретного формата
        /// </summary>
        [ContextMethod("СодержитФормат", "ContainsFormat")]
        public bool ContainsFormat(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            try
            {
                return RunInSta(() =>
                {
                    IDataObject data = Clipboard.GetDataObject();
                    return data != null && data.GetDataPresent(format);
                });
            }
            catch
            {
                // A transient clipboard lock means the requested format is currently unavailable.
            }

            return false;
        }

        /// <summary>
        /// Получить данные в конкретном формате
        /// </summary>
        [ContextMethod("ПолучитьДанныеФормата", "GetData")]
        public string GetData(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                throw new RuntimeException("Формат не может быть пустым");
            }

            try
            {
                return RunInSta(() =>
                {
                    IDataObject data = Clipboard.GetDataObject();
                    if (data != null && data.GetDataPresent(format))
                    {
                        object obj = data.GetData(format);
                        if (obj != null)
                        {
                            return obj.ToString();
                        }
                    }

                    return string.Empty;
                });
            }
            catch (Exception ex)
            {
                throw new RuntimeException($"Ошибка при получении данных формата '{format}': {ex.Message}");
            }
        }

        /// <summary>
        /// Генерация случайного имени файла для изображения в пользовательском каталоге
        /// </summary>
        private static string GenerateImageFileName()
        {
            return TemporaryFileStore.CreatePath("clipboard_image", "png");
        }

        /// <summary>
        /// Определить формат изображения по расширению файла
        /// </summary>
        private static ImageFormat GetImageFormatFromExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return ImageFormat.Jpeg;

                case ".png":
                    return ImageFormat.Png;

                case ".bmp":
                    return ImageFormat.Bmp;

                case ".gif":
                    return ImageFormat.Gif;

                case ".tiff":
                case ".tif":
                    return ImageFormat.Tiff;

                case ".ico":
                    return ImageFormat.Icon;

                default:
                    return ImageFormat.Png; // По умолчанию PNG
            }
        }
    }
}
