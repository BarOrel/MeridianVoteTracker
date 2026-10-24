import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ImportClassification, ImportReportDto, ImportRowsPageDto } from '../api/models';
import { ImportsService } from '../api/services';

@Component({
  selector: 'app-import-report',
  imports: [RouterLink, DatePipe, DecimalPipe],
  template: `
    <p><a routerLink="/imports">← Imports</a></p>
    @if (report(); as r) {
      <section>
        <h2>Import report · {{ r.import?.fileName }}</h2>
        <p class="muted">
          Uploaded by {{ r.import?.uploadedBy }} · {{ r.import?.uploadedAt | date: 'yyyy-MM-dd HH:mm' }} ·
          {{ r.summary?.totalRows | number }} rows · {{ r.votes?.length }} votes ·
          <span class="badge">{{ r.import?.state }}</span>
        </p>
        <div class="cards">
          <div class="card"><b>{{ r.summary?.newRows | number }}</b>New rows</div>
          <div class="card"><b>{{ r.summary?.unchangedRows | number }}</b>Unchanged</div>
          <div class="card"><b>{{ r.summary?.duplicateRows | number }}</b>Duplicates
            <span class="muted">({{ r.summary?.exactDuplicateRows }} exact, {{ r.summary?.duplicatesAfterNormalization }} after normalization)</span></div>
          <div class="card"><b>{{ r.summary?.invalidRows | number }}</b>Invalid</div>
          <div class="card"><b>{{ r.summary?.contradictionRows | number }}</b>Contradictions</div>
          <div class="card"><b>{{ r.summary?.conflictRows | number }}</b>Conflict rows</div>
          <div class="card"><b>{{ r.summary?.conflictsCreated | number }}</b>Conflicts created
            <span class="muted">({{ r.summary?.ballotConflictsCreated }} ballot, {{ r.summary?.metadataConflictsCreated }} details)</span></div>
          <div class="card"><b>{{ r.summary?.normalizedRows | number }}</b>Normalized rows</div>
        </div>
        @if ((r.summary?.conflictRows ?? 0) > 0 || (r.summary?.conflictsCreated ?? 0) > 0) {
          <div class="alert">
            <b>Approved data was not changed.</b> Differences from approved data were sent to <a routerLink="/review">Review</a>.
            @if (r.summary?.rowsLinkedToPendingConflicts) { {{ r.summary?.rowsLinkedToPendingConflicts }} rows matched conflicts already waiting for a decision (not opened twice). }
          </div>
        }
      </section>

      <section>
        <h3>Per vote</h3>
        <table>
          <tr><th>Vote</th><th>Outcome</th><th>New</th><th>Unchanged</th><th>Dup</th><th>Invalid</th><th>Contradiction</th><th>Conflict</th><th>Notes</th></tr>
          @for (v of r.votes?.slice(0, voteLimit()); track v.assemblyVoteId) {
            <tr>
              <td>@if (v.voteId) { <a [routerLink]="['/votes', v.voteId]">{{ v.assemblyVoteId }}</a> } @else { {{ v.assemblyVoteId }} }</td>
              <td><span class="badge" [class.ok]="v.outcome === 'NewVote' || v.outcome === 'CountriesAdded'" [class.warn]="v.outcome === 'ConflictsDetected'" [class.bad]="v.outcome === 'BlockedByContradiction'">{{ v.outcome }}</span></td>
              <td>{{ v.newRows }}</td><td>{{ v.unchangedRows }}</td><td>{{ v.duplicateRows }}</td><td>{{ v.invalidRows }}</td>
              <td>{{ v.contradictionRows }}</td><td>{{ v.conflictRows }}</td>
              <td class="muted">@for (n of v.notes; track $index) { <div>{{ n }}</div> }</td>
            </tr>
          }
        </table>
        @if ((r.votes?.length ?? 0) > voteLimit()) {
          <button (click)="voteLimit.set(voteLimit() + 200)">Show more votes</button>
        }
      </section>
    }

    <section>
      <h3>Rows that were not stored as-is</h3>
      <div class="row">
        Filter:
        <select (change)="filter($any($event.target).value)">
          <option value="">Not stored as-is (all)</option>
          @for (c of classifications; track c) { <option [value]="c">{{ c }}</option> }
          <option value="normalized">Normalized (stored, with notes)</option>
        </select>
        @if (rows(); as page) { <span class="muted">{{ page.totalCount | number }} rows</span> }
      </div>
      @if (rows(); as page) {
        <table>
          <tr><th>Row</th><th>Vote</th><th>Country</th><th>Value</th><th>Outcome</th><th>Reason</th><th>Original</th></tr>
          @for (row of page.rows; track row.rowNumber) {
            <tr>
              <td>{{ row.rowNumber }}</td><td>{{ row.assemblyVoteId }}</td><td>{{ row.countryCode }}</td><td>{{ row.ballot }}</td>
              <td><span class="badge">{{ row.classification }}</span></td>
              <td>{{ row.message }} @for (n of row.normalizationNotes; track $index) { <div class="muted">{{ n }}</div> }</td>
              <td class="mono">{{ row.originalText }}</td>
            </tr>
          }
        </table>
        <div class="row">
          <button [disabled]="page.page === 1" (click)="loadRows(page.page! - 1)">Previous</button>
          <span class="muted">Page {{ page.page }}</span>
          <button [disabled]="(page.page ?? 1) * (page.pageSize ?? 100) >= (page.totalCount ?? 0)" (click)="loadRows(page.page! + 1)">Next</button>
        </div>
      }
    </section>
  `,
})
export class ImportReportPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly report = signal<ImportReportDto | null>(null);
  protected readonly rows = signal<ImportRowsPageDto | null>(null);
  protected readonly voteLimit = signal(200);
  protected readonly classifications = Object.values(ImportClassification);
  private classification?: ImportClassification;
  private normalizedOnly = false;
  private readonly api = inject(ImportsService);

  ngOnInit(): void {
    this.api.apiImportsIdReportGet({ id: this.id() }).subscribe((r) => this.report.set(r));
    this.loadRows(1);
  }

  protected filter(value: string): void {
    this.normalizedOnly = value === 'normalized';
    this.classification = this.normalizedOnly ? ImportClassification.New : (value as ImportClassification) || undefined;
    this.loadRows(1);
  }

  protected loadRows(page: number): void {
    this.api
      .apiImportsIdRowsGet({ id: this.id(), classification: this.classification, normalizedOnly: this.normalizedOnly, page, pageSize: 100 })
      .subscribe((rows) => this.rows.set(rows));
  }
}
