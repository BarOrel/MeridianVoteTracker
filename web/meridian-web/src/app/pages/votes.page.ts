import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { VoteSummaryDto } from '../api/models';
import { VotesService } from '../api/services';

@Component({
  selector: 'app-votes',
  imports: [RouterLink],
  template: `
    <section>
      <h2>Votes</h2>
      <div class="row">
        Session <input type="number" style="width:80px" (change)="session = $any($event.target).value || undefined" />
        From <input type="date" (change)="from = $any($event.target).value || undefined" />
        To <input type="date" (change)="to = $any($event.target).value || undefined" />
        <label><input type="checkbox" (change)="withoutPosition = $any($event.target).checked" /> Without position only</label>
        <button class="primary" (click)="load()">Apply</button>
      </div>
      @if (missingPosition() > 0) {
        <div class="alert bad"><b>{{ missingPosition() }} vote(s) have no position.</b> Votes without a position are never silently included in a snapshot.</div>
      }
      <table>
        <tr><th>Vote</th><th>Date</th><th>Title</th><th>Country votes</th><th>Position</th><th>Weight</th><th>Open conflicts</th></tr>
        @for (v of votes(); track v.id) {
          <tr>
            <td><a [routerLink]="['/votes', v.id]">{{ v.assemblyVoteId }}</a></td>
            <td>{{ v.voteDate }}</td><td>{{ v.title }}</td><td>{{ v.countryVotes }}</td>
            <td>@if (v.position) { {{ v.position }} } @else { <span class="badge bad">No position</span> }</td>
            <td>{{ v.weight ?? '—' }}</td>
            <td>@if (v.pendingConflicts) { <span class="badge warn">{{ v.pendingConflicts }} pending</span> } @else { — }</td>
          </tr>
        } @empty {
          <tr><td colspan="7" class="muted">No votes.</td></tr>
        }
      </table>
      <p class="muted">Showing at most 2,000 votes; use the filters to narrow down.</p>
    </section>
  `,
})
export class VotesPage implements OnInit {
  protected readonly votes = signal<VoteSummaryDto[]>([]);
  protected readonly missingPosition = signal(0);
  protected session?: number;
  protected from?: string;
  protected to?: string;
  protected withoutPosition = false;
  private readonly api = inject(VotesService);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api
      .apiVotesGet({ session: this.session ? Number(this.session) : undefined, from: this.from, to: this.to, withoutPosition: this.withoutPosition })
      .subscribe((votes) => {
        this.votes.set(votes);
        this.missingPosition.set(votes.filter((v) => !v.position).length);
      });
  }
}
