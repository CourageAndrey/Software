using System.Text.RegularExpressions;

namespace Software.Notepad
{
	public static class TextSearch
	{
		public static Regex Pattern(string pattern, bool matchCase = false, bool regex = false, bool wholeWord = false)
		{
			if (pattern.Length == 0)
			{
				throw new ArgumentException("Enter text to find.");
			}

			string expression = regex ? pattern : Regex.Escape(pattern);
			if (wholeWord)
			{
				expression = @"(?<!\w)(?:" + expression + @")(?!\w)";
			}

			return new Regex(expression, RegexOptions.Multiline | RegexOptions.CultureInvariant
				| (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(250));
		}

		public static Match Find(string text, string pattern, int start, bool matchCase = false, bool regex = false, bool wholeWord = false)
		{
			var search = Pattern(pattern, matchCase, regex, wholeWord);
			var match = search.Match(text, Math.Clamp(start, 0, text.Length));
			return match.Success ? match : search.Match(text);
		}

		public static (string Text, int Count) ReplaceAll(string text, string pattern, string replacement,
			bool matchCase = false, bool regex = false, bool wholeWord = false)
		{
			var search = Pattern(pattern, matchCase, regex, wholeWord);
			int count = 0;
			string result = search.Replace(text, match =>
			{
				count++;
				return regex ? match.Result(replacement) : replacement;
			});
			return (result, count);
		}
	}
}