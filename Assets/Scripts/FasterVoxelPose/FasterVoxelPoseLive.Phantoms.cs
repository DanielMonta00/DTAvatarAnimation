using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Phantoms: skeletons of people who are not there.
//
// What they are. The network finds people by lifting every view's 2D joint heatmaps into one 3D volume and looking for peaks. With few
// cameras, evidence from different people (or the same person along a ray) lines up at a place nobody stands, and the 3D stages
// still answer with a full skeleton. Measured on the recorded session (Server~/fvp_phantoms.py, frames 0..607, ground truth from the
// avatars' rigs): with 3 cameras 31 % of all skeletons are phantoms and 59 % of the frames show at least one; with 4 cameras 7 % and 15 %.
// The network's own score does not separate them (a phantom's median 0.11-0.13, a real person's 0.18) without removing real people.
//
// What tells them apart. The 3D stages never look at the images again; the 2D heatmaps they started from do. The server reads those
// heatmaps back at the projection of the skeleton's joints in every view ("support": the strongest value within a few cells, averaged
// over the joints; Engine.person_support) and a phantom has little of it in at least one view: a real person's mean support over the views
// is 0.5-0.7, a phantom's is mostly under 0.3. Rule: a skeleton whose support averaged over the views is under `minSupport`
// is a phantom. On the recorded session (Server~/fvp_phantom_rules.py), at 0.35:
//
//                      phantoms removed    real people removed (their mean error is 441 mm / 273 mm, against 160 / 154 mm for those kept)
//     3 cameras             91 %                 3.5 %          frames with a phantom: 59 % -> 6 %
//     4 cameras            100 %                 2.0 %          frames with a phantom: 15 % -> 0 %
//
// Hidden phantoms are kept apart (FvpFrame.phantoms) and counted in the status card, so the rule can be checked against what you see.
public partial class FasterVoxelPoseLive
{
    public enum PhantomMode
    {
        Off = 0,    // every skeleton the network returns is a person
        Mark = 1,   // phantoms stay in People but are drawn faint and flagged in the status card
        Hide = 2,   // phantoms are taken out of People, the overlays and the gizmos; the status card says how many
    }

    [Header("Phantoms (skeletons of people who are not there)")]
    [Tooltip("What to do with a skeleton the 2D heatmaps do not back. Hide: out of People, the displays and the gizmos (counted in the status card). Mark: kept, drawn faint, flagged. Off: no filter. Needs the server of this version (it sends the 2D support); an older one is restarted once.")]
    public PhantomMode phantomFilter = PhantomMode.Hide;
    [Range(0.05f, 0.8f)]
    [Tooltip("A skeleton whose mean 2D support over the views (how strongly each view's own heatmaps back its joints, 0-1) is under this is a phantom. 0.35 removed 91 % of the phantoms and 3.5 % of the real people on the recorded session with 3 cameras (100 % / 2 % with 4); real people are at 0.5-0.7.")]
    public float minSupport = 0.35f;

    public const int RequiredServerVersion = 2;       // 2: 2D support + stage times in the answers
    [Tooltip("A server left running from before this script was updated does not send the 2D support the phantom filter needs: restart it once (only a server this editor started can be restarted; otherwise the Console says how).")]
    public bool restartOutdatedServer = true;

    int serverVersion;
    bool restartedOutdated;
    int estimatesSeen, phantomsHidden, phantomsMarked, estimatesWithSupport;

    // The server said hello. One that predates this script is restarted once, so the new answers (2D support, stage times) are there.
    void OnServerVersion()
    {
        if (serverVersion >= RequiredServerVersion) return;
        if (restartOutdatedServer && !restartedOutdated && FvpServerProcess.IsRunning)
        {
            restartedOutdated = true;
            Debug.LogWarning($"[FasterVoxelPose] the running server is version {serverVersion}, this script needs {RequiredServerVersion} (2D support for the phantom filter, stage times): restarting it once.", this);
            FvpServerProcess.Stop();      // the link drops, ManageServer starts the current one
            serverLaunched = false; serverError = null;
            return;
        }
        Debug.LogWarning($"[FasterVoxelPose] the running server is version {serverVersion}, this script needs {RequiredServerVersion}: the phantom filter and the stage times are off. " +
                         "Tools > FasterVoxelPose > Stop inference server, then Play again.", this);
    }

    public int PhantomsHidden => phantomsHidden;
    public bool ServerHasSupport => serverVersion >= RequiredServerVersion;

    // [[row, view0, view1, ...], ...] -> support values by row of the poses table (null where the server sent none)
    static float[][] ReadSupport(FvpMessage m, int rows)
    {
        if (m.json == null || !m.json.TryGetValue("support", out object o) || !(o is List<object> list)) return null;
        var byRow = new float[rows][];
        foreach (object entry in list)
        {
            if (!(entry is List<object> e) || e.Count < 2 || !(e[0] is double r)) continue;
            int row = (int)r;
            if (row < 0 || row >= rows) continue;
            var v = new float[e.Count - 1];
            for (int k = 1; k < e.Count; k++) v[k - 1] = e[k] is double d ? (float)d : -1f;
            byRow[row] = v;
        }
        return byRow;
    }

    static float MeanSupport(float[] v)
    {
        float sum = 0f; int n = 0;
        for (int i = 0; i < v.Length; i++) if (v[i] >= 0f) { sum += v[i]; n++; }
        return n > 0 ? sum / n : -1f;
    }

    // After BuildPeople: decide who is a phantom, and take them out of f.people when hiding.
    void EvaluatePhantoms(FvpFrame f)
    {
        f.phantoms.Clear();
        estimatesSeen++;
        bool any = false;
        for (int i = 0; i < f.people.Count; i++) if (f.people[i].meanSupport >= 0f) { any = true; break; }
        if (any) estimatesWithSupport++;
        if (phantomFilter == PhantomMode.Off) return;

        for (int i = f.people.Count - 1; i >= 0; i--)
        {
            FvpPerson p = f.people[i];
            if (p.meanSupport < 0f || p.meanSupport >= minSupport) continue;
            p.phantom = true;
            p.phantomWhy = WhyPhantom(p);
            if (phantomFilter == PhantomMode.Hide)
            {
                f.people.RemoveAt(i);
                f.phantoms.Insert(0, p);
                phantomsHidden++;
            }
            else phantomsMarked++;
        }
    }

    string WhyPhantom(FvpPerson p)
    {
        int weakest = 0;
        for (int v = 1; v < p.support.Length; v++) if (p.support[v] >= 0f && (p.support[weakest] < 0f || p.support[v] < p.support[weakest])) weakest = v;
        string cam = cameras != null && weakest < cameras.Count && cameras[weakest] != null ? cameras[weakest].name.Trim() : "view " + weakest;
        return $"2D support {p.meanSupport:F2} < {minSupport:F2}, weakest {cam} {p.support[weakest]:F2}";
    }

    // The status card's note about the filter.
    void AppendPhantomLine(StringBuilder sb, FvpFrame f)
    {
        if (phantomFilter == PhantomMode.Off || f == null || f.placeholder) return;
        if (!ServerHasSupport && serverVersion > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Phantom filter inactive: the running server is older than this script\n(Tools > FasterVoxelPose > Stop inference server, then Play).");
            return;
        }
        if (f.phantoms.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(f.phantoms.Count).Append(f.phantoms.Count == 1 ? " phantom hidden: " : " phantoms hidden: ").Append(f.phantoms[0].phantomWhy);
        }
        else if (phantomFilter == PhantomMode.Mark)
        {
            int flagged = 0;
            for (int i = 0; i < f.people.Count; i++) if (f.people[i].phantom) flagged++;
            if (flagged > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(flagged).Append(flagged == 1 ? " phantom flagged (faint)" : " phantoms flagged (faint)");
            }
        }
    }
}
