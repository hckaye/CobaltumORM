CREATE TABLE cobaltum_benchmark_posts (
    id integer PRIMARY KEY,
    author_id integer NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    created_at timestamp without time zone NOT NULL,
    score integer NOT NULL
);
