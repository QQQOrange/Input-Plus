using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using InputPlus.Core.Qbar.Search;

namespace InputPlus.UI.Windows;

internal static class QbarTitleHighlight
{
	public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached("Segments", typeof(IReadOnlyList<QbarTitleSegment>), typeof(QbarTitleHighlight), new PropertyMetadata((object)null, (PropertyChangedCallback)OnSegmentsChanged));

	public static void SetSegments(DependencyObject element, IReadOnlyList<QbarTitleSegment>? value)
	{
		element.SetValue(SegmentsProperty, (object)value);
	}

	public static IReadOnlyList<QbarTitleSegment>? GetSegments(DependencyObject element)
	{
		return (IReadOnlyList<QbarTitleSegment>)element.GetValue(SegmentsProperty);
	}

	public static IReadOnlyList<QbarTitleSegment> Segments(string? text, IReadOnlyList<QbarHighlight>? marks)
	{
		string text2 = text ?? "";
		if (text2.Length == 0 || marks == null || marks.Count == 0)
		{
			return new List<QbarTitleSegment>(1)
			{
				new QbarTitleSegment(text2, match: false)
			};
		}
		List<QbarTitleSegment> list = new List<QbarTitleSegment>(marks.Count * 2 + 1);
		int num = 0;
		foreach (QbarHighlight mark in marks)
		{
			if (mark.Start < 0 || mark.Length <= 0 || mark.Start < num)
			{
				continue;
			}
			int num2 = Math.Min(mark.Start, text2.Length);
			int num3 = Math.Min(num2 + mark.Length, text2.Length);
			if (num3 > num2)
			{
				if (num2 > num)
				{
					list.Add(new QbarTitleSegment(text2.Substring(num, num2 - num), match: false));
				}
				list.Add(new QbarTitleSegment(text2.Substring(num2, num3 - num2), match: true));
				num = num3;
			}
		}
		if (num < text2.Length)
		{
			list.Add(new QbarTitleSegment(text2.Substring(num), match: false));
		}
		if (list.Count == 0)
		{
			list.Add(new QbarTitleSegment(text2, match: false));
		}
		return list;
	}

	private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (!(d is TextBlock textBlock))
		{
			return;
		}
		textBlock.Inlines.Clear();
		if (!(e.NewValue is IReadOnlyList<QbarTitleSegment> { Count: not 0 } readOnlyList))
		{
			return;
		}
		for (int i = 0; i < readOnlyList.Count; i++)
		{
			QbarTitleSegment qbarTitleSegment = readOnlyList[i];
			Run run = new Run(qbarTitleSegment.Text);
			if (qbarTitleSegment.Match)
			{
				run.FontWeight = FontWeights.SemiBold;
				run.TextDecorations = TextDecorations.Underline;
			}
			textBlock.Inlines.Add(run);
		}
	}

	static QbarTitleHighlight()
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected Obj, but got Unknown
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Expected Obj, but got Unknown
	}
}
