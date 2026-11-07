import { Route, Routes } from "react-router-dom";
import { Shell } from "./components/Shell";
import { EmptyState } from "./components/States";
import { ComparePage } from "./pages/ComparePage";
import { RunDetailPage } from "./pages/RunDetailPage";
import { TrendPage } from "./pages/TrendPage";

export function App() {
  return (
    <Routes>
      <Route element={<Shell />}>
        <Route index element={<ComparePage />} />
        <Route path="runs/:id" element={<RunDetailPage />} />
        <Route path="trend" element={<TrendPage />} />
        <Route path="*" element={<EmptyState title="Page not found" />} />
      </Route>
    </Routes>
  );
}
