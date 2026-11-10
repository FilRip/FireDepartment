using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Input;
using OpenFD.Formats;

namespace OpenFD.MG;

// éditeur : annuler (Ctrl+Z) et rétablir (Ctrl+Y, Ctrl+Maj+Z). L'état de la mission est photographié chaque fois qu'il a
// changé et que l'on a fini son geste (boutons de la souris lâchés, flèches, Pg.Préc / Pg.Suiv, A et Q relâchées) :
// un objet glissé, une suite de crans au clavier, un relief peint d'un seul trait font chacun une seule étape.
public sealed partial class OpenFDGame
{
    private sealed class EdSnapshot
    {
        public required List<EditorObject> Objects;
        public required List<LevelObject> Level;
        public required string Mis;
        public required string Name;
        public required bool Packed;
        public float[]? Heights;
        public ushort[]? ZoneA;
    }

    private const int MaxUndo = 100;
    private readonly List<EdSnapshot> _edUndo = new();
    private readonly Stack<EdSnapshot> _edRedo = new();
    private EdSnapshot? _edUndoLast;   // l'état d'après la dernière étape

    private EdSnapshot EdTakeSnapshot() => new EdSnapshot
    {
        Objects = _edObjects.Select(o => o.Copy()).ToList(),
        Level = new List<LevelObject>(_edLevel.Objects),
        Mis = _edMisText,
        Name = _edName,
        Packed = _edPacked,
        Heights = _session.Grid?.GroundHeight.ToArray(),
        ZoneA = _session.Grid?.ZoneA.ToArray(),
    };

    /// <summary>Mission ouverte : pas d'étape à annuler ; l'état de départ est photographié.</summary>
    private void EdUndoReset()
    {
        _edUndo.Clear();
        _edRedo.Clear();
        _edUndoLast = _edLoaded ? EdTakeSnapshot() : null;
    }

    /// <summary>L'état actuel est-il celui de la photo ?</summary>
    private bool EdSameAs(EdSnapshot s)
    {
        if (s.Objects.Count != _edObjects.Count || s.Level.Count != _edLevel.Objects.Count || s.Mis != _edMisText || s.Name != _edName || s.Packed != _edPacked)
            return false;
        for (int i = 0; i < s.Objects.Count; i++)
            if (!_edObjects[i].SameAs(s.Objects[i]))
                return false;
        for (int i = 0; i < s.Level.Count; i++)
            if (!ReferenceEquals(s.Level[i], _edLevel.Objects[i]))
                return false;
        if (_session.Grid is { } g && s.Heights != null && s.ZoneA != null)
        {
            if (!MemoryMarshal.AsBytes(g.GroundHeight.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(s.Heights.AsSpan())))
                return false;
            if (!g.ZoneA.AsSpan().SequenceEqual(s.ZoneA))
                return false;
        }
        return true;
    }

    /// <summary>Geste en cours : un bouton de la souris, ou une touche qui déplace, monte ou peint le relief, encore enfoncé.</summary>
    private bool EdGestureOn()
    {
        MouseState m = Mouse.GetState();
        if (m.LeftButton == ButtonState.Pressed || m.RightButton == ButtonState.Pressed || m.MiddleButton == ButtonState.Pressed)
            return true;
        foreach (Keys key in new[] { Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.PageUp, Keys.PageDown, Keys.A, Keys.Q })
            if (_edKeys.IsKeyDown(key))
                return true;
        return _edDrag != null || _edMoving;
    }

    /// <summary>Chaque image : si la mission a changé et que le geste est fini, une étape de plus à annuler.</summary>
    private void EdUndoTrack(bool force)
    {
        if (!_edLoaded || _edUndoLast == null || (!force && EdGestureOn()) || EdSameAs(_edUndoLast))
            return;
        _edUndo.Add(_edUndoLast);
        if (_edUndo.Count > MaxUndo)
            _edUndo.RemoveAt(0);
        _edRedo.Clear();
        _edUndoLast = EdTakeSnapshot();
    }

    private void EdUndo()
    {
        if (!_edLoaded || _edUndoLast == null)
            return;
        EdCancelFocus();
        EdUndoTrack(true);
        if (_edUndo.Count == 0)
        {
            EdMessage(Tr("UndoNone", "Rien à annuler"));
            return;
        }
        _edRedo.Push(_edUndoLast);
        _edUndoLast = _edUndo[^1];
        _edUndo.RemoveAt(_edUndo.Count - 1);
        EdRestore(_edUndoLast);
        EdMessage(Tr("Undone", "Annulé ({0} étape(s) restante(s))", _edUndo.Count));
    }

    private void EdRedo()
    {
        if (!_edLoaded || _edUndoLast == null)
            return;
        EdCancelFocus();
        EdUndoTrack(true);
        if (_edRedo.Count == 0)
        {
            EdMessage(Tr("RedoNone", "Rien à rétablir"));
            return;
        }
        _edUndo.Add(_edUndoLast);
        _edUndoLast = _edRedo.Pop();
        EdRestore(_edUndoLast);
        EdMessage(Tr("Redone", "Rétabli ({0} étape(s) à rétablir)", _edRedo.Count));
    }

    /// <summary>Champ de texte en cours abandonné (pas validé) : il ne doit pas réécrire l'état qu'on retrouve.</summary>
    private void EdCancelFocus()
    {
        _edFocusId = null;
        _edFocusCommit = null;
    }

    /// <summary>Remet la mission dans l'état de la photo (objets recopiés : la photo reste intacte).</summary>
    private void EdRestore(EdSnapshot s)
    {
        uint? selId = _edSel?.Source.Id;
        _edObjects.Clear();
        _edObjects.AddRange(s.Objects.Select(o => o.Copy()));
        _edLevel.Objects.Clear();
        _edLevel.Objects.AddRange(s.Level);
        _edMisText = s.Mis;
        _edName = s.Name;
        _edPacked = s.Packed;
        if (_session.Grid is { } g && s.Heights != null && s.ZoneA != null && s.Heights.Length == g.GroundHeight.Length)
        {
            Array.Copy(s.Heights, g.GroundHeight, s.Heights.Length);
            Array.Copy(s.ZoneA, g.ZoneA, s.ZoneA.Length);
            if (_noScenery)
                EdRebuildTerrain();
        }
        foreach (EditorObject e in _edObjects)
            EdPlace(e);
        _edTreesDirty = true;
        _edSel = null;
        if (selId is { } id && _edObjects.FirstOrDefault(o => o.Source.Id == id) is { } again)
            _edSel = again;
    }
}
