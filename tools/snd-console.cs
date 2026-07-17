// FMOD sound browser for the UnityExplorer C# console (F7).
// PASTE THE WHOLE BLOCK ONCE, press Run. Then use:
//   Snd.Find("stick")   -> numbered list of event paths containing "stick"
//   Snd.Play(3)         -> play result #3 at your position
//   Snd.Play("event:/player/foley/pickup")  -> play an exact path
// Game window must be FOCUSED to hear anything (minimized = silent).

public static class Snd
{
    public static System.Collections.Generic.List<string> Found = new System.Collections.Generic.List<string>();

    public static string Find(string key)
    {
        Found.Clear();
        var sb = new System.Text.StringBuilder();
        var lo = key.ToLower();
        foreach (var kv in FMOD_StudioSystem._loadedBanks)
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FMOD.Studio.EventDescription> evs = null;
            try { kv.Value.getEventList(out evs); } catch { continue; }
            if (evs == null) continue;
            foreach (var ed in evs)
            {
                string p = null;
                try { var e2 = ed; e2.getPath(out p); } catch { continue; }
                if (p == null || !p.ToLower().Contains(lo)) continue;
                if (Found.Contains(p)) continue;
                sb.Append(Found.Count).Append(": ").Append(p).Append("\n");
                Found.Add(p);
            }
        }
        return sb.Length > 0 ? sb.ToString() : "ничего не нашлось по '" + key + "'";
    }

    public static string Play(int i)
    {
        if (i < 0 || i >= Found.Count) return "нет такого номера, сперва Snd.Find(...)";
        return Play(Found[i]);
    }

    public static string Play(string path)
    {
        var pos = Assemblies.Sons.FMOD.FMODUtils.GetListenerPosition();
        FMODCommon.PlayOneshot(path, pos);
        return "♪ " + path;
    }
}
