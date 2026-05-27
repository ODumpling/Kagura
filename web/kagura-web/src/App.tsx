import { BrowserRouter, NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { SourcesPage } from './pages/SourcesPage';
import { WorkItemsPage } from './pages/WorkItemsPage';
import { WorkItemDetailPage } from './pages/WorkItemDetailPage';
import './App.css';

const navClass = ({ isActive }: { isActive: boolean }) =>
  `px-2 py-1 rounded text-sm ${
    isActive ? 'bg-slate-800 text-slate-100' : 'text-slate-400 hover:text-slate-200'
  }`;

export default function App() {
  return (
    <BrowserRouter>
      <div className="flex flex-col min-h-screen">
        <header className="flex items-center gap-6 px-6 py-3 border-b border-slate-700 bg-slate-900">
          <div className="font-bold tracking-wide">Kagura</div>
          <nav className="flex gap-2">
            <NavLink to="/sources" className={navClass}>Sources</NavLink>
            <NavLink to="/workitems" className={navClass}>Work items</NavLink>
          </nav>
        </header>
        <main className="px-6 py-6 max-w-[1400px] w-full mx-auto flex-1">
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
