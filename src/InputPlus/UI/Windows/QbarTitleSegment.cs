namespace InputPlus.UI.Windows;

public sealed class QbarTitleSegment
{
	public string Text { get; }

	public bool Match { get; }

	public QbarTitleSegment(string text, bool match)
	{
		Text = text;
		Match = match;
	}
}
