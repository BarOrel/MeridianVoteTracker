import { Routes } from '@angular/router';
import { ImportReportPage } from './pages/import-report.page';
import { ImportsPage } from './pages/imports.page';
import { ReviewPage } from './pages/review.page';
import { SnapshotDetailPage } from './pages/snapshot-detail.page';
import { SnapshotsPage } from './pages/snapshots.page';
import { VoteDetailPage } from './pages/vote-detail.page';
import { VotesPage } from './pages/votes.page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'imports' },
  { path: 'imports', component: ImportsPage },
  { path: 'imports/:id', component: ImportReportPage },
  { path: 'review', component: ReviewPage },
  { path: 'votes', component: VotesPage },
  { path: 'votes/:id', component: VoteDetailPage },
  { path: 'snapshots', component: SnapshotsPage },
  { path: 'snapshots/:id', component: SnapshotDetailPage },
];
