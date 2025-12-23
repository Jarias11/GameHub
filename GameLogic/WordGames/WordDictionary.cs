using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using System.Reflection;

public static class WordDictionary
{
    private static readonly HashSet<string> words;

    static WordDictionary()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("GameLogic.Resources.enable1.txt");
        using var reader = new StreamReader(stream);
        words = reader.ReadToEnd()
                       .Split('\n')
                       .Select(w => w.Trim().ToLower())
                       .ToHashSet();
    }

    public static bool IsValid(string word) =>
        words.Contains(word.ToLower());

        public static IEnumerable<string> AllWords => words;
}