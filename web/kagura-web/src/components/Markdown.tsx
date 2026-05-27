import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { cn } from '@/lib/utils';

export function Markdown({ children, className }: { children: string; className?: string }) {
  return (
    <div className={cn('text-sm leading-relaxed text-foreground', className)}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          h1: ({ node, ...props }) => <h1 className="mt-4 mb-2 text-lg font-semibold first:mt-0" {...props} />,
          h2: ({ node, ...props }) => <h2 className="mt-4 mb-2 text-base font-semibold first:mt-0" {...props} />,
          h3: ({ node, ...props }) => <h3 className="mt-3 mb-2 text-sm font-semibold first:mt-0" {...props} />,
          h4: ({ node, ...props }) => <h4 className="mt-3 mb-1 text-sm font-semibold first:mt-0" {...props} />,
          p: ({ node, ...props }) => <p className="my-2 first:mt-0 last:mb-0" {...props} />,
          ul: ({ node, ...props }) => <ul className="my-2 ml-5 list-disc space-y-1" {...props} />,
          ol: ({ node, ...props }) => <ol className="my-2 ml-5 list-decimal space-y-1" {...props} />,
          li: ({ node, ...props }) => <li className="leading-relaxed" {...props} />,
          a: ({ node, ...props }) => (
            <a className="text-primary underline underline-offset-2 hover:no-underline" target="_blank" rel="noreferrer" {...props} />
          ),
          code: ({ node, className, children, ...props }) => {
            const isInline = !className?.includes('language-');
            if (isInline) {
              return (
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs" {...props}>
                  {children}
                </code>
              );
            }
            return (
              <code className={cn('font-mono text-xs', className)} {...props}>
                {children}
              </code>
            );
          },
          pre: ({ node, ...props }) => (
            <pre className="my-2 overflow-x-auto rounded-md border bg-muted/40 p-3 text-xs" {...props} />
          ),
          blockquote: ({ node, ...props }) => (
            <blockquote className="my-2 border-l-2 border-border pl-3 text-muted-foreground" {...props} />
          ),
          hr: ({ node, ...props }) => <hr className="my-3 border-border" {...props} />,
          table: ({ node, ...props }) => (
            <div className="my-2 overflow-x-auto">
              <table className="w-full border-collapse text-xs" {...props} />
            </div>
          ),
          th: ({ node, ...props }) => <th className="border border-border bg-muted/40 px-2 py-1 text-left font-semibold" {...props} />,
          td: ({ node, ...props }) => <td className="border border-border px-2 py-1" {...props} />,
          img: ({ node, ...props }) => <img className="my-2 max-w-full rounded-md" {...props} />,
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}
