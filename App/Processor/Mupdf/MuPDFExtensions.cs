﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using PDFPatcher.Common;

namespace MuPDF.Extensions
{
	public static class MuPDFExtensions
	{
		#region 文档基础结构
		public static TObj Get<TObj>(this PdfArray array, int index) where TObj : PdfObject {
			return array[index].UnderlyingObject as TObj;
		}
		public static TObj Get<TObj>(this PdfDictionary dict, int index) where TObj : PdfObject {
			return dict[index].Value.UnderlyingObject as TObj;
		}
		public static TObj Get<TObj>(this PdfDictionary dict, PdfNames key) where TObj : PdfObject {
			return dict[key].UnderlyingObject as TObj;
		}
		public static TObj Get<TObj>(this PdfDictionary dict, PdfNames key, PdfNames alias) where TObj : PdfObject {
			return dict.GetValue(key, alias).UnderlyingObject as TObj;
		}
		#endregion

		#region 几何尺寸
		public static RectangleF ToRectangleF(this Box box) {
			return RectangleF.FromLTRB(box.X0, box.Y0, box.X1, box.Y1);
		}
		public static Rectangle ToRectangle(this Box box) {
			return Rectangle.FromLTRB((int)box.X0, (int)box.Y0, (int)box.X1, (int)box.Y1);
		}
		public static void Deconstruct(this Point point, out float x, out float y) {
			x = point.X; y = point.Y;
		}

		public static bool IsHorizontalNeighbor(this Box me, Box other) {
			if (me.IsEmpty || other.IsInfinite || other.IsEmpty || me.IsInfinite || other.Y0 > me.Y1 || other.Y1 < me.Y0) {
				return false;
			}
			var h = me.Height / other.Height;
			return h > 0.4 && h < 2.5;
		}
		#endregion

		public static string GetText(this TextLine textLine) {
			var sb = new StringBuilder(10);
			foreach (var ch in textLine) {
				sb.Append(Char.IsSurrogate((char)ch.Character) ? "?" : char.ConvertFromUtf32(ch.Character));
			}
			return sb.ToString();
		}

		#region 渲染页面
		public static Bitmap RenderBitmapPage(this Page page, int width, int height, ImageRendererOptions options, Cookie cookie) {
			using (var pix = InternalRenderPage(page, width, height, options, cookie)) {
				if (pix != null) {
					return pix.ToBitmap(options);
				}
			}
			return null;
		}

		static Pixmap InternalRenderPage(Page page, int width, int height, ImageRendererOptions options, Cookie cookie) {
			var b = page.Bound;
			if (b.Width == 0 || b.Height == 0) {
				return null;
			}
			var ctm = CalculateMatrix(page, width, height, options);
			var bbox = width > 0 && height > 0 ? new BBox(0, 0, width, height) : b.Transform(ctm).Round();

			var pix = Context.Instance.CreatePixmap(options.ColorSpace == ColorspaceKind.None ? ColorspaceKind.Rgb : options.ColorSpace, bbox);
			if (pix == null) {
				throw new MuException($"无法渲染页面：{(page.PageNumber + 1).ToText()}");
			}
			pix.Clear(0xFF);
			try {
				using (var dev = Device.NewDraw(pix, Matrix.Identity)) {
					if (options.LowQuality) {
						dev.EnableDeviceHints(DeviceHints.DontInterpolateImages | DeviceHints.NoCache);
					}
					if (cookie.IsCancellationPending) {
						return null;
					}
					page.RunContents(dev, ctm, cookie);
					if (options.HideAnnotations == false) {
						page.RunAnnotations(dev, ctm, cookie);
						page.RunWidgets(dev, ctm, cookie);
					}
					dev.Close();

					if (cookie.IsCancellationPending) {
						return null;
					}
					if (options.TintColor != Color.Transparent) {
						pix.Tint(options.TintColor.ToArgb());
					}
					if (options.Gamma != 1.0f) {
						pix.Gamma(options.Gamma);
					}
					return pix;
				}
			}
			catch {
				pix.Dispose();
				throw;
			}
		}

		static Matrix CalculateMatrix(Page page, int width, int height, ImageRendererOptions options) {
			float w = width, h = height;
			var b = page.Bound;
			if (options.UseSpecificWidth) {
				if (w < 0) {
					w = -w;
				}
				if (h < 0) {
					h = -h;
				}
				if (options.FitArea && w != 0 && h != 0) {
					var rw = w / b.Width;
					var rh = h / b.Height;
					if (rw < rh) {
						h = 0;
					}
					else {
						w = 0;
					}
				}
				if (w == 0 && h == 0) { // No resize
					w = b.Width;
					h = b.Height;
				}
				else if (h == 0) {
					h = width * b.Height / b.Width;
				}
				else if (w == 0) {
					w = height * b.Width / b.Height;
				}
			}
			else if (w == 0 || h == 0) {
				w = b.Width * options.ScaleRatio * options.Dpi / 72;
				h = b.Height * options.ScaleRatio * options.Dpi / 72;
			}

			var ctm = Matrix.Scale(w / b.Width, h / b.Height).RotateTo(options.Rotation);
			if (options.VerticalFlipImages) {
				ctm = ctm.Concat(Matrix.VerticalFlip);
			}
			if (options.HorizontalFlipImages) {
				ctm = ctm.Concat(Matrix.HorizontalFlip);
			}
			return ctm;
		}

		/// <summary>
		/// 将 Pixmap 的数据转换为 <see cref="Bitmap"/>。
		/// </summary>
		public static unsafe Bitmap ToBitmap(this Pixmap pix, ImageRendererOptions options) {
			int width = pix.Width;
			int height = pix.Height;
			bool grayscale = options.ColorSpace == ColorspaceKind.Gray;
			bool invert = options.InvertColor;
			var bmp = new Bitmap(width, height, grayscale ? PixelFormat.Format8bppIndexed : PixelFormat.Format24bppRgb);
			var imageData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, bmp.PixelFormat);
			var ptrSrc = (byte*)pix.Samples;
			var ptrDest = (byte*)imageData.Scan0;
			if (grayscale) {
				var palette = bmp.Palette;
				for (int i = 0; i < 256; ++i)
					palette.Entries[i] = Color.FromArgb(i, i, i);
				bmp.Palette = palette;
				for (int y = 0; y < height; y++) {
					var pl = ptrDest;
					var sl = ptrSrc;
					for (int x = 0; x < width; x++) {
						*pl = invert ? (byte)(*sl ^ 0xFF) : *sl;
						pl++;
						sl++;
					}
					ptrDest += imageData.Stride;
					ptrSrc = sl;
				}
			}
			else { // DeviceBGR
				for (int y = 0; y < height; y++) {
					var pl = ptrDest;
					var sl = ptrSrc;
					if (invert) {
						for (int x = 0; x < width; x++) {
							// 在这里进行 RGB 到 DIB BGR 的转换（省去 Mupdf 内部的转换工作）
							pl[2] = (byte)(*sl ^ 0xFF); sl++; // R
							pl[1] = (byte)(*sl ^ 0xFF); sl++; // G
							pl[0] = (byte)(*sl ^ 0xFF); sl++; // B
							pl += 3;
						}
					}
					else {
						for (int x = 0; x < width; x++) {
							// 在这里进行 RGB 到 DIB BGR 的转换（省去 Mupdf 内部的转换工作）
							pl[2] = *sl; sl++; // R
							pl[1] = *sl; sl++; // G
							pl[0] = *sl; sl++; // B
							pl += 3;
						}
					}
					ptrDest += imageData.Stride;
					ptrSrc = sl;
				}
			}
			bmp.UnlockBits(imageData);
			if (options.Dpi > 0) {
				bmp.SetResolution(options.Dpi, options.Dpi);
			}
			return bmp;
		}
		#endregion

		#region 支持加载未嵌入字体
		static readonly FzLoadSystemFont __LoadSystemFont = RequestSystemFont;
		static readonly FzLoadSystemCjkFont __LoadSystemCjkFont = RequestSystemCjkFont;
		static readonly FzLoadSystemFallbackFont __LoadSystemFallbackFont = RequestSystemFallbackFont;
		static bool __FontLookupHooked;
		[DllImport("MuPDFLib.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "fz_new_font_from_file", CharSet = CharSet.Ansi)]
		internal static extern IntPtr LoadFontFromFile(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string path, int index, int useGlyphBox);
		public static void HootSystemFontLookup(this Context context) {
			if (__FontLookupHooked == false) {
				context.InstallLoadSystemFontFuncs(__LoadSystemFont, __LoadSystemCjkFont, __LoadSystemFallbackFont);
				__FontLookupHooked = true;
			}
		}
		static IntPtr RequestSystemFont(IntPtr ctx, string name, int bold, int italic, int needExactMetrics) {
			System.Diagnostics.Debug.WriteLine("Requesting system font: " + name);
			var f = TryLoadCompatibleFont(name);
			if (f != null) {
				return LoadFontFromFile(ctx, null, f, 0, 0);
			}
			return IntPtr.Zero;
		}
		static IntPtr RequestSystemCjkFont(IntPtr ctx, string name, int registry, int serifDesired) {
			System.Diagnostics.Debug.WriteLine("Requesting system CJK font: " + name);
			var ff = TryLoadCompatibleFont(name);
			return ff != null ? LoadFontFromFile(ctx, name, ff, 0, 1) : IntPtr.Zero;
		}

		static string TryLoadCompatibleFont(string name) {
			var ff = System.IO.Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.System)) + "\\Fonts\\";
			if (name[0] == '@') {
				name = name.Substring(1);
			}
			return name.StartsWith("SimKai", StringComparison.OrdinalIgnoreCase) || name.StartsWith("楷体_GB2312", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Kaiti_GB2312", StringComparison.OrdinalIgnoreCase) ? ff + "simkai.ttf"
				: name.StartsWith("SimSun", StringComparison.OrdinalIgnoreCase) || name.StartsWith("宋体", StringComparison.OrdinalIgnoreCase) || name.StartsWith("STSong", StringComparison.OrdinalIgnoreCase) ? ff + "simsun.ttc"
				: name.StartsWith("SimHei", StringComparison.OrdinalIgnoreCase) || name.StartsWith("黑体", StringComparison.OrdinalIgnoreCase) ? ff + "simhei.ttf"
				: name.StartsWith("SimLi", StringComparison.OrdinalIgnoreCase) || name.StartsWith("隶书", StringComparison.OrdinalIgnoreCase) ? ff + "simli.ttf"
				: name.StartsWith("SimFang", StringComparison.OrdinalIgnoreCase) || name.StartsWith("仿宋_GB2312", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Fangsong_GB2312", StringComparison.OrdinalIgnoreCase) ? ff + "simfang.ttf"
				: name.StartsWith("SimYou", StringComparison.OrdinalIgnoreCase) || name.StartsWith("幼圆", StringComparison.OrdinalIgnoreCase) ? ff + "simyou.ttf"
				: null;
		}

		static IntPtr RequestSystemFallbackFont(IntPtr ctx, int script, int language, int serif, int bold, int italic) {
			System.Diagnostics.Debug.WriteLine("Requesting fallback font: " + script);
			return IntPtr.Zero;
		}
		#endregion
	}
}