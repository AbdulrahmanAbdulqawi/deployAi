import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { DeploymentRepoChangesFlowComponent } from './deployment-repo-changes-flow.component';

/**
 * DeployAI opened the pull request and then offered the merge only while this component stayed
 * alive. A refresh lost it, and the only way left to finish was GitHub — the manual step the flow
 * exists to remove, reached by pressing F5. Regenerating instead opens a second pull request for
 * the same work, which is how TicketHub ended up with two.
 */
describe('DeploymentRepoChangesFlowComponent', () => {
  let fixture: ComponentFixture<DeploymentRepoChangesFlowComponent>;
  let http: HttpTestingController;

  const pendingUrl = '/api/github/repos/acme/app/deployment-setup/pending?ref=master';

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeploymentRepoChangesFlowComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])]
    }).compileComponents();

    fixture = TestBed.createComponent(DeploymentRepoChangesFlowComponent);
    http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('repo', 'app');
    fixture.componentRef.setInput('runGenerate', () => of());
  });

  afterEach(() => http.verify());

  it('offers the merge again for a setup pull request that is still open', () => {
    fixture.componentRef.setInput('mergeMode', 'setup');
    fixture.componentRef.setInput('baseBranch', 'master');
    fixture.detectChanges();

    http.expectOne(pendingUrl).flush({
      pending: {
        branchName: 'deployai/setup-20260922-123537',
        pullRequestNumber: 30,
        pullRequestUrl: 'https://github.com/acme/app/pull/30'
      }
    });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('deployai/setup-20260922-123537');
    expect(text).toContain('Merge into master');
    // Said plainly, because this one was recovered rather than produced in front of the user.
    expect(text).toContain('DeployAI already prepared these changes');
  });

  it('shows nothing extra when the repository has no setup waiting', () => {
    fixture.componentRef.setInput('mergeMode', 'setup');
    fixture.componentRef.setInput('baseBranch', 'master');
    fixture.detectChanges();

    http.expectOne(pendingUrl).flush({ pending: null });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Merge into master');
  });

  /**
   * A lookup that fails leaves the panel as it was. This is an offer to finish something, not a
   * step — an error banner over a working Generate button would be noise about a pull request
   * that may not exist.
   */
  it('stays quiet when the lookup fails', () => {
    fixture.componentRef.setInput('mergeMode', 'setup');
    fixture.componentRef.setInput('baseBranch', 'master');
    fixture.detectChanges();

    http.expectOne(pendingUrl).error(new ProgressEvent('network'));
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Merge into');
  });

  /** The fix flow has its own pull requests and must not be handed a setup one. */
  it('does not look for a setup pull request in the fix flow', () => {
    fixture.componentRef.setInput('mergeMode', 'fix');
    fixture.componentRef.setInput('baseBranch', 'master');
    fixture.detectChanges();

    http.expectNone(pendingUrl);
  });
});
