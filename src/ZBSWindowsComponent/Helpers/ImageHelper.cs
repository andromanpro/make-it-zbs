using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ZBSWindowsComponent.Helpers
{
    /// <summary>
    /// Вспомогательные методы для работы с изображениями
    /// </summary>
    internal static class ImageHelper
    {
        /// <summary>
        /// Конвертировать изображение в указанный формат
        /// </summary>
        public static void ConvertImage(string sourcePath, string destinationPath, ImageFormat format, int quality = 90)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Файл не найден: {sourcePath}");
            }

            using (Image image = Image.FromFile(sourcePath))
            {
                SaveImage(image, destinationPath, format, quality);
            }
        }

        /// <summary>
        /// Сохранить изображение с указанным качеством
        /// </summary>
        public static void SaveImage(Image image, string path, ImageFormat format, int quality = 90)
        {
            // Убедиться что директория существует
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Для JPEG используем EncoderParameters для контроля качества
            if (format.Equals(ImageFormat.Jpeg))
            {
                SaveJpegWithQuality(image, path, quality);
            }
            else
            {
                image.Save(path, format);
            }
        }

        /// <summary>
        /// Сохранить JPEG с контролем качества
        /// </summary>
        private static void SaveJpegWithQuality(Image image, string path, int quality)
        {
            // Получить JPEG encoder
            ImageCodecInfo jpegCodec = GetEncoder(ImageFormat.Jpeg);
            if (jpegCodec == null)
            {
                // Fallback: сохранить без параметров качества
                image.Save(path, ImageFormat.Jpeg);
                return;
            }

            // Создать EncoderParameters
            EncoderParameters encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

            image.Save(path, jpegCodec, encoderParams);
        }

        /// <summary>
        /// Получить ImageCodecInfo для формата
        /// </summary>
        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();

            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }

            return null;
        }

        /// <summary>
        /// Изменить размер изображения
        /// </summary>
        public static Image ResizeImage(Image image, int width, int height, bool maintainAspectRatio = true)
        {
            if (maintainAspectRatio)
            {
                // Вычислить новые размеры с сохранением пропорций
                double ratioX = (double)width / image.Width;
                double ratioY = (double)height / image.Height;
                double ratio = Math.Min(ratioX, ratioY);

                width = (int)(image.Width * ratio);
                height = (int)(image.Height * ratio);
            }

            Bitmap resized = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                graphics.DrawImage(image, 0, 0, width, height);
            }

            return resized;
        }

        /// <summary>
        /// Изменить размер изображения из файла
        /// </summary>
        public static void ResizeImageFile(string sourcePath, string destinationPath, int width, int height, bool maintainAspectRatio = true)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Файл не найден: {sourcePath}");
            }

            using (Image original = Image.FromFile(sourcePath))
            {
                using (Image resized = ResizeImage(original, width, height, maintainAspectRatio))
                {
                    // Определить формат
                    ImageFormat format = GetImageFormatFromExtension(destinationPath);
                    SaveImage(resized, destinationPath, format);
                }
            }
        }

        /// <summary>
        /// Конвертировать изображение в Base64
        /// </summary>
        public static string ImageToBase64(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"Файл не найден: {imagePath}");
            }

            byte[] imageBytes = File.ReadAllBytes(imagePath);
            return Convert.ToBase64String(imageBytes);
        }

        /// <summary>
        /// Конвертировать Base64 в изображение
        /// </summary>
        public static void Base64ToImage(string base64String, string outputPath)
        {
            byte[] imageBytes = Convert.FromBase64String(base64String);

            using (MemoryStream ms = new MemoryStream(imageBytes))
            {
                using (Image image = Image.FromStream(ms))
                {
                    ImageFormat format = GetImageFormatFromExtension(outputPath);
                    SaveImage(image, outputPath, format);
                }
            }
        }

        /// <summary>
        /// Получить информацию об изображении
        /// </summary>
        public static ImageInfo GetImageInfo(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"Файл не найден: {imagePath}");
            }

            using (Image image = Image.FromFile(imagePath))
            {
                return new ImageInfo
                {
                    Width = image.Width,
                    Height = image.Height,
                    Format = image.RawFormat.ToString(),
                    HorizontalResolution = image.HorizontalResolution,
                    VerticalResolution = image.VerticalResolution,
                    PixelFormat = image.PixelFormat.ToString(),
                    FileSizeBytes = new FileInfo(imagePath).Length
                };
            }
        }

        /// <summary>
        /// Определить формат изображения по расширению файла
        /// </summary>
        public static ImageFormat GetImageFormatFromExtension(string filePath)
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

                case ".emf":
                    return ImageFormat.Emf;

                case ".wmf":
                    return ImageFormat.Wmf;

                case ".exif":
                    return ImageFormat.Exif;

                default:
                    return ImageFormat.Png; // По умолчанию PNG
            }
        }

        /// <summary>
        /// Информация об изображении
        /// </summary>
        public class ImageInfo
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public string Format { get; set; }
            public float HorizontalResolution { get; set; }
            public float VerticalResolution { get; set; }
            public string PixelFormat { get; set; }
            public long FileSizeBytes { get; set; }
        }
    }
}
