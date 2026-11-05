namespace HelixPythonNamespace.UI.Interaction
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class SelectionManager
    {
        private readonly List<NodeVisual> _selectedNodes;
        private readonly List<EdgeVisual> _selectedEdges;

        public SelectionManager()
        {
            _selectedNodes = new List<NodeVisual>();
            _selectedEdges = new List<EdgeVisual>();
        }

        public event EventHandler SelectionChanged;

        public IList<NodeVisual> SelectedNodes
        {
            get { return _selectedNodes.AsReadOnly(); }
        }

        public IList<EdgeVisual> SelectedEdges
        {
            get { return _selectedEdges.AsReadOnly(); }
        }

        public bool IsEmpty
        {
            get { return _selectedNodes.Count == 0 && _selectedEdges.Count == 0; }
        }

        public void Select(NodeVisual node)
        {
            if (node == null)
            {
                return;
            }

            if (!_selectedNodes.Contains(node))
            {
                _selectedNodes.Add(node);
                node.IsSelected = true;
                OnSelectionChanged();
            }
        }

        public void Select(EdgeVisual edge)
        {
            if (edge == null)
            {
                return;
            }

            if (!_selectedEdges.Contains(edge))
            {
                _selectedEdges.Add(edge);
                edge.IsSelected = true;
                OnSelectionChanged();
            }
        }

        public void Deselect(NodeVisual node)
        {
            if (node == null)
            {
                return;
            }

            if (_selectedNodes.Remove(node))
            {
                node.IsSelected = false;
                OnSelectionChanged();
            }
        }

        public void Deselect(EdgeVisual edge)
        {
            if (edge == null)
            {
                return;
            }

            if (_selectedEdges.Remove(edge))
            {
                edge.IsSelected = false;
                OnSelectionChanged();
            }
        }

        public void ToggleSelect(NodeVisual node)
        {
            if (node == null)
            {
                return;
            }

            if (_selectedNodes.Contains(node))
            {
                Deselect(node);
            }
            else
            {
                Select(node);
            }
        }

        public void ToggleSelect(EdgeVisual edge)
        {
            if (edge == null)
            {
                return;
            }

            if (_selectedEdges.Contains(edge))
            {
                Deselect(edge);
            }
            else
            {
                Select(edge);
            }
        }

        public void ClearSelection()
        {
            if (_selectedNodes.Count == 0 && _selectedEdges.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _selectedNodes.Count; i++)
            {
                _selectedNodes[i].IsSelected = false;
            }

            for (int i = 0; i < _selectedEdges.Count; i++)
            {
                _selectedEdges[i].IsSelected = false;
            }

            _selectedNodes.Clear();
            _selectedEdges.Clear();
            OnSelectionChanged();
        }

        private void OnSelectionChanged()
        {
            EventHandler handler = SelectionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
