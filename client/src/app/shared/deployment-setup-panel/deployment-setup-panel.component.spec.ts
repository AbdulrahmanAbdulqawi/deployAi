import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { DeploymentSetupPanelComponent } from './deployment-setup-panel.component';
import { DeploymentReadinessResult } from '../../core/models/api.models';

/**
 * The panel named one shape for both. A compose app — whose whole definition is that it is *not*
 * split across two origins — was told it was getting a "split-origin deployment setup", and
 * offered "Generate setup with AI" for files that now come from the deployment graph whatever the
 * AI preference says. Both sentences describe a deployment the user is not getting.
 */
describe('DeploymentSetupPanelComponent', () => {
  let fixture: ComponentFixture<DeploymentSetupPanelComponent>;

  const readiness = (usesSingleOriginCompose: boolean): DeploymentReadinessResult => ({
    isReady: false,
    usesSplitOrigin: !usesSingleOriginCompose,
    usesSingleOriginCompose,
    warnings: [],
    missingFiles: [
      { path: 'docker-compose.coolify.yml', reason: 'A Docker Compose file is required.', severity: 'blocking' }
    ]
  });

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeploymentSetupPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();

    fixture = TestBed.createComponent(DeploymentSetupPanelComponent);
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('repo', 'app');
    fixture.componentRef.setInput('gitRef', 'main');
    fixture.componentRef.setInput('parts', []);
  });

  it('names the shape the user is actually getting', () => {
    fixture.componentRef.setInput('readiness', readiness(true));
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Single-origin deployment setup');
    expect(text).not.toContain('Split-origin deployment setup');
  });

  it('still says split-origin for a split-origin deployment', () => {
    fixture.componentRef.setInput('readiness', readiness(false));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Split-origin deployment setup');
  });

  it('does not promise AI generation for files the graph writes', () => {
    fixture.componentRef.setInput('readiness', readiness(true));
    fixture.detectChanges();

    expect(fixture.componentInstance.generateLabel()).toBe('Set these up for me');
  });

  it('keeps the AI wording where AI is what runs', () => {
    fixture.componentRef.setInput('readiness', readiness(false));
    fixture.detectChanges();

    expect(fixture.componentInstance.generateLabel()).toBe('Generate setup with AI');
  });
});
