using UdonSharp.CE.Editor.Inspector;
using UdonSharpEditor;

// Register CEBehaviourEditor as the default inspector for UdonSharpBehaviours
[assembly: DefaultUdonSharpBehaviourEditor(typeof(CEBehaviourEditor), "CE Inspector")]

