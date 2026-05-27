import { BrowserRouter, NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { SourcesPage } from './pages/SourcesPage';
import { WorkItemsPage } from './pages/WorkItemsPage';
import { WorkItemDetailPage } from './pages/WorkItemDetailPage';
import './App.css';

export default function App() {
  return (
    <BrowserRouter>
      <div className="layout">
        <header className="topbar">
          <div className="brand">Kagura</div>
          <nav>
            <NavLink to="/sources">Sources</NavLink>
            <NavLink to="/workitems">Work items</NavLink>
          </nav>
        </header>
        <main>
          <Routes>
            <Route path="/" element={<Navigate to="/workitems" replace />} />
            <Route path="/sources" element={<SourcesPage />} />
            <Route path="/workitems" element={<WorkItemsPage />} />
            <Route path="/workitems/:id" element={<WorkItemDetailPage />} />
          </Routes>
        </main>
      </div>
    </BrowserRouter>
  );
}
