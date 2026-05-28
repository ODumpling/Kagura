import clsx from 'clsx';
import Heading from '@theme/Heading';
import styles from './styles.module.css';

const FeatureList = [
  {
    title: 'One inbox, every tracker',
    description: (
      <>
        Pull issues from GitHub, Azure DevOps, Beads, and Markdown sources into
        a single work-item list. Triage where the work is, not where the
        ticket lives.
      </>
    ),
  },
  {
    title: 'Triage with the claude CLI',
    description: (
      <>
        Kagura shells out to your logged-in <code>claude</code> CLI to break a
        work item into a small, ordered set of tasks. No API key required, and
        you can edit, reorder, or drop tasks before approving.
      </>
    ),
  },
  {
    title: 'Parallel worktree agents',
    description: (
      <>
        Each task runs in its own git worktree as a real <code>claude</code>
        {' '}PTY session. Attach over SignalR + xterm.js, type to steer, and
        let Ralph Loop carry the whole work item to a pull request.
      </>
    ),
  },
];

function Feature({title, description}) {
  return (
    <div className={clsx('col col--4')}>
      <div className="text--center padding-horiz--md">
        <Heading as="h3">{title}</Heading>
        <p>{description}</p>
      </div>
    </div>
  );
}

export default function HomepageFeatures() {
  return (
    <section className={styles.features}>
      <div className="container">
        <div className="row">
          {FeatureList.map((props, idx) => (
            <Feature key={idx} {...props} />
          ))}
        </div>
      </div>
    </section>
  );
}
