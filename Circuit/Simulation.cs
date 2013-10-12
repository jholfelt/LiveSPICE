﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Reflection.Emit;
using SyMath;
using LinqExpressions = System.Linq.Expressions;
using LinqExpression = System.Linq.Expressions.Expression;
using ParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace Circuit
{
    /// <summary>
    /// Simulate a circuit.
    /// </summary>
    public class Simulation
    {
        // Expression for t at the previous timestep.
        private static readonly Expression t0 = Variable.New("t0");
        private static readonly Expression t = Component.t;
        
        // This is used often enough to shorten it a few characters.
        private static readonly Arrow t_t0 = Arrow.New(t, t0);

        protected double _t = 0.0;
        /// <summary>
        /// Get the current time of the simulation.
        /// </summary>
        public double Time { get { return _t; } }

        protected double _T;
        /// <summary>
        /// Get the timestep for the simulation.
        /// </summary>
        public double TimeStep { get { return _T; } }

        protected int iterations;
        /// <summary>
        /// Get or set the maximum number of iterations to use when numerically solving equations.
        /// </summary>
        public int Iterations { get { return iterations; } }

        protected int oversample;
        /// <summary>
        /// Get or set the oversampling factor for the simulation.
        /// </summary>
        public int Oversample { get { return oversample; } }
        
        private List<Expression> nodes;
        /// <summary>
        /// Enumerate the nodes in the simulation.
        /// </summary>
        public IEnumerable<Expression> Nodes { get { return nodes; } }

        private ILog log = new ConsoleLog();
        /// <summary>
        /// Get or set the log associated with this simulation.
        /// </summary>
        public ILog Log { get { return log; } set { log = value; } }

        // Simulation timestep.
        private Expression h;

        // Expressions for trivial solutions to the system.
        private List<Arrow> trivial;
        // Expressions for the solution of the differential equations.
        private List<Arrow> differential;
        // Expressions for the solution of the linear equations.
        private List<Arrow> linear;
        // Implicit equations describing the non-linear system.
        private List<Equal> nonlinear;
        private List<Arrow> f0;
        private List<Expression> unknowns;
        // Expressions for the voltage of each two terminal component.
        private List<Arrow> components;

        // Check if x is used anywhere in the simulation, including the outputs.
        private bool IsExpressionUsed(IEnumerable<Expression> Extra, Expression x)
        {
            return
                Extra.Any(i => i.IsFunctionOf(x)) ||
                trivial.Any(i => i.Right.IsFunctionOf(x)) ||
                linear.Any(i => i.Right.IsFunctionOf(x)) ||
                differential.Any(i => i.Right.IsFunctionOf(x)) ||
                components.Any(i => i.Right.IsFunctionOf(x)) ||
                nonlinear.Any(i => i.IsFunctionOf(x));
        }

        // Stores any global state in the simulation (previous state values, mostly).
        private Dictionary<Expression, GlobalExpr<double>> globals = new Dictionary<Expression, GlobalExpr<double>>();

        // Given a system of potentially non-linear equations f, extract the non-linear expressions and replace them with a variable f0.
        // f0 is constructed such that ExtractNonLinear(f, x, f0).Evaluate(f0) == f.
        private static List<Equal> ExtractNonLinear(List<Equal> f, List<Expression> x, List<Arrow> f0)
        {
            List<Equal> ex = new List<Equal>();
            foreach (Equal i in f)
            {
                // Gather list of linear and non-linear terms.
                List<Expression> linear = new List<Expression>();
                List<Expression> nonlinear = new List<Expression>();
                foreach (Expression t in Add.TermsOf((i.Left - i.Right).Expand()))
                {
                    if (t.IsFunctionOf(x) && !IsLinearFunctionOf(t, x))
                        nonlinear.Add(t);
                    else
                        linear.Add(t);
                }

                // If there are any non-linear terms, create a token variable for them and add them to the linear system.
                if (nonlinear.Any())
                {
                    Variable f0i = Variable.New("f0_" + f0.Count.ToString());
                    linear.Add(f0i);
                    f0.Add(Arrow.New(f0i, Add.New(nonlinear)));

                    ex.Add(Equal.New(Add.New(linear), Constant.Zero));
                }
                else
                {
                    ex.Add(i);
                }
            }

            return ex;
        }
        
        /// <summary>
        /// Create a simulation for the given circuit.
        /// </summary>
        /// <param name="C">Circuit to simulate.</param>
        /// <param name="T">Sampling period.</param>
        public Simulation(Circuit Circuit, Quantity SampleRate, int Oversample, int Iterations, ILog Log)
        {
            log = Log;
            oversample = Oversample;
            iterations = Iterations;
            _T = 1.0 / (double)SampleRate;
            nodes = Circuit.Nodes.Select(i => (Expression)Call.New(((Call)i.V).Target, t)).ToList();

            // Length of one timestep in the oversampled simulation.
            h = 1 / ((Expression)SampleRate * Oversample);

            log.WriteLine(MessageType.Info, "--------");
            LogTime(MessageType.Info, "Building simulation for circuit '" + Circuit.Name + "'", true);
            log.WriteLine(MessageType.Info, "  Sample Rate: " + SampleRate.ToString());
            log.WriteLine(MessageType.Info, "  Oversample: " + Oversample);
            log.WriteLine(MessageType.Info, "  Iterations: " + Iterations);

            LogTime(MessageType.Info, "Performing MNA on circuit...");

            // Analyze the circuit to get the MNA system and unknowns.
            List<Expression> y = new List<Expression>();
            List<Equal> mna = new List<Equal>();
            Circuit.Analyze(mna, y);
            LogTime(MessageType.Info, "Done.");
            LogExpressions("System:", mna);
            LogExpressions("Unknowns:", y);

            LogTime(MessageType.Info, "Solving system...");

            // Find trivial solutions for y and substitute them into the system.
            trivial = mna.Solve(y);
            trivial.RemoveAll(i => i.Right.IsFunctionOf(y));
            mna = mna.Evaluate(trivial).OfType<Equal>().ToList();
            y.RemoveAll(i => trivial.Any(j => j.Left.Equals(i)));
            LogExpressions("Trivial solutions:", trivial);


            // Separate the non-linear equations of the MNA system to a separate system.
            f0 = new List<Arrow>();
            mna = ExtractNonLinear(mna, y, f0);


            // Separate y into differential and algebraic unknowns.
            List<Expression> dy_dt = y.Where(i => IsD(i, t)).ToList();
            y.RemoveAll(i => IsD(i, t));
            // Find the solutions to the differential unknowns.
            List<Expression> ya = y.Where(i => dy_dt.None(j => DOf(j).Equals(i))).ToList();
            differential = mna
                // Solve for the algebraic unknowns in terms of the rest and substitute them.
                .Evaluate(mna.Solve(ya)).OfType<Equal>()
                // Solve the resulting system of differential equations.
                .NDSolve(dy_dt.Select(i => DOf(i)), t, t0, h, IntegrationMethod.Trapezoid);
            y.RemoveAll(i => differential.Any(j => j.Left.Equals(i)));
            LogExpressions("Differential solutions:", differential);
            
            // After solving for the differential unknowns, divide them by h so we don't have to do it during simulation.
            // It's faster to simulate, and we get the benefits of arbitrary precision calculations here.
            mna = mna.Evaluate(dy_dt.Select(i => Arrow.New(i, i / h))).Cast<Equal>().ToList();

            // Create global variables for the previous value of each differential solution.
            foreach (Arrow i in differential)
                globals[i.Left.Evaluate(t, t0)] = new GlobalExpr<double>(0.0, i.Left.ToString().Replace("[t]", "[t-1]"));


            // Find as many closed form solutions in the remaining system as possible.
            linear = mna.Solve(y.Where(i => f0.None(j => j.Right.IsFunctionOf(i))));
            y.RemoveAll(i => linear.Any(j => j.Left.Equals(i)));
            linear.RemoveAll(i => i.Right.IsFunctionOf(y));
            LogExpressions("Linear solutions:", linear);

            // The remaining non-linear system will be solved via Newton's method.
            nonlinear = mna
                .Where(i => i.IsFunctionOf(f0.Select(j => j.Left)))
                .Evaluate(f0).OfType<Equal>().ToList();
            unknowns = y;
            // Create global variables for the non-linear unknowns.
            foreach (Expression i in y)
                globals[i.Evaluate(t, t0)] = new GlobalExpr<double>(0.0, i.ToString().Replace("[t]", "[t-1]"));
            // Create a global variable for the value of each f0.
            foreach (Arrow i in f0)
                globals[i.Left] = new GlobalExpr<double>(0.0, i.Left.ToString());

            LogExpressions("Non-linear system:", nonlinear);
            LogExpressions("Unknowns:", unknowns);

            LogTime(MessageType.Info, "System solved.");
            
            // Add solutions for the voltage across all the components.
            components = Circuit.Components.OfType<TwoTerminal>()
                .Select(i => Arrow.New(Call.New(ExprFunction.New(i.Name, t), t), i.V))
                .ToList();
            LogExpressions("Component voltages:", components);
        }
        
        /// <summary>
        /// Clear all state from the simulation.
        /// </summary>
        public void Reset()
        {
            _t = 0.0;
            foreach (GlobalExpr<double> i in globals.Values)
                i.Value = 0.0;
        }

        /// <summary>
        /// Process some samples with this simulation.
        /// </summary>
        /// <param name="N">Number of samples to process.</param>
        /// <param name="Input">Mapping of node Expression -> double[] buffers that describe the input samples.</param>
        /// <param name="Output">Mapping of node Expression -> double[] buffers that describe requested output samples.</param>
        /// <param name="Arguments">Constant expressions describing the values of any parameters to the simulation.</param>
        public void Process(int N, IDictionary<Expression, double[]> Input, IDictionary<Expression, double[]> Output, IEnumerable<Arrow> Arguments)
        {
            Delegate processor = Compile(Input.Keys, Output.Keys, Arguments.Select(i => i.Left));

            // Build parameter list for the processor.
            List<object> parameters = new List<object>(3 + Input.Count + Output.Count + Arguments.Count());
            parameters.Add(N);
            parameters.Add((double)_t);
            parameters.Add((double)_T);
            parameters.Add(Oversample);
            parameters.Add(Iterations);
            foreach (KeyValuePair<Expression, double[]> i in Input)
                parameters.Add(i.Value);
            foreach (KeyValuePair<Expression, double[]> i in Output)
                parameters.Add(i.Value);
            foreach (Arrow i in Arguments)
                parameters.Add((double)i.Right);

            _t = (double)processor.DynamicInvoke(parameters.ToArray());

            //// Check the last samples for infinity/NaN.
            //foreach (KeyValuePair<Expression, double[]> i in Output)
            //{
            //    double v = i.Value[i.Value.Length - 1];
            //    if (double.IsInfinity(v) || double.IsNaN(v))
            //    {
            //        Log.WriteLine(MessageType.Error, "Simulation diverged at " + _t.ToString() + " s.");
            //        Reset();
            //        return;
            //    }
            //}
        }

        /// <summary>
        /// Process some samples with this simulation.
        /// </summary>
        /// <param name="N">Number of samples to process.</param>
        /// <param name="Input">Mapping of node Expression -> double[] buffers that describe the input samples.</param>
        /// <param name="Output">Mapping of node Expression -> double[] buffers that describe requested output samples.</param>
        /// <param name="Arguments">Constant expressions describing the values of any parameters to the simulation.</param>
        public void Process(int N, IDictionary<Expression, double[]> Input, IDictionary<Expression, double[]> Output, params Arrow[] Arguments)
        {
            Process(N, Input, Output, Arguments.AsEnumerable());
        }

        public void Process(Expression InputNode, double[] InputSamples, IDictionary<Expression, double[]> Output)
        {
            Process(
                InputSamples.Length,
                new Dictionary<Expression, double[]>() { { InputNode, InputSamples } },
                Output);
        }

        public void Process(Expression InputNode, double[] InputSamples, Expression OutputNode, double[] OutputSamples)
        {
            Process(
                InputSamples.Length,
                new Dictionary<Expression, double[]>() { { InputNode, InputSamples } },
                new Dictionary<Expression, double[]>() { { OutputNode, OutputSamples } });
        }

        Dictionary<long, Delegate> compiled = new Dictionary<long, Delegate>();
        // Compile and cache delegates for processing various IO configurations for this simulation.
        private Delegate Compile(IEnumerable<Expression> Input, IEnumerable<Expression> Output, IEnumerable<Expression> Parameters)
        {
            long hash = Input.OrderedHashCode();
            hash = hash * 33 + Output.OrderedHashCode();
            hash = hash * 33 + Parameters.OrderedHashCode();

            Delegate d;
            if (compiled.TryGetValue(hash, out d))
                return d;

            //LogTime(MessageType.Info, "Compiling simulation...", true);
            //LogExpressions("Input:", Input);
            //LogExpressions("Output:", Output);
            //LogExpressions("Parameters:", Parameters);

            //LogTime(MessageType.Info, "Defining lambda...");
            LinqExpressions.LambdaExpression lambda = DefineProcessFunction(Input, Output, Parameters);
            //LogTime(MessageType.Info, "Compiling lambda...");
            d = lambda.Compile();
            //LogTime(MessageType.Info, "Done.");

            return compiled[hash] = d;
        }
        
        // The resulting lambda processes N samples, using buffers provided for Input and Output:
        //  double Process(int N, double t0, double T, double[] Input0 ..., double[] Output0 ..., double Parameter0 ...)
        //  { ... }
        private LinqExpressions.LambdaExpression DefineProcessFunction(IEnumerable<Expression> Input, IEnumerable<Expression> Output, IEnumerable<Expression> Parameters)
        {
            // Map expressions to identifiers in the syntax tree.
            Dictionary<Expression, LinqExpression> map = new Dictionary<Expression, LinqExpression>();
            Dictionary<Expression, LinqExpression> buffers = new Dictionary<Expression, LinqExpression>();

            // Add the globals to the map.
            foreach (KeyValuePair<Expression, GlobalExpr<double>> i in globals)
                map[i.Key] = i.Value;
            
            // Lambda definition objects.
            List<ParameterExpression> parameters = new List<ParameterExpression>();
            List<ParameterExpression> locals = new List<ParameterExpression>();
            List<LinqExpression> body = new List<LinqExpression>();

            // Create parameters for the basic simulation info (N, t, T, Oversample, Iterations).
            ParameterExpression SampleCount = Declare<int>(parameters, "SampleCount");
            ParameterExpression t0 = Declare<double>(parameters, map, Simulation.t0);
            ParameterExpression T = Declare<double>(parameters, map, Component.T);
            ParameterExpression Oversample = Declare<int>(parameters, "Oversample");
            ParameterExpression Iterations = Declare<int>(parameters, "Iterations");
            // Create buffer parameters for each input, output.
            foreach (Expression i in Input.Concat(Output))
                Declare<double[]>(parameters, buffers, i);
            // Create constant parameters for simulation parameters.
            foreach (Expression i in Parameters)
                Declare<double>(parameters, map, i);

            // Remove any inputs that aren't used anywhere in the system.
            Input = Input.Where(i => IsExpressionUsed(Output, i)).ToList();
            // Create globals to store previous values of input.
            foreach (Expression i in Input)
            {
                GlobalExpr<double> prev = new GlobalExpr<double>(i.ToString().Replace("[t]", "[t-1]"));
                globals[i] = prev;
                map[i.Evaluate(t_t0)] = prev;
            }

            // Define lambda body.

            // double t = t0
            ParameterExpression t = Declare<double>(locals, map, Simulation.t);
            body.Add(LinqExpression.Assign(t, t0));

            // double h = T / Oversample
            ParameterExpression h = Declare<double>(locals, "h");
            body.Add(LinqExpression.Assign(h, LinqExpression.Divide(T, LinqExpression.Convert(Oversample, typeof(double)))));

            // double invOversample = 1 / (double)Oversample
            ParameterExpression invOversample = Declare<double>(locals, "invOversample");
            body.Add(LinqExpression.Assign(invOversample, Reciprocal(LinqExpression.Convert(Oversample, typeof(double)))));

            // Trivial timestep expressions that are not a function of the input can be set once here (outside the sample loop).
            // This might not be necessary if you trust the .Net expression compiler to lift this invariant code out of the loop.
            foreach (Arrow i in trivial.Where(i => !i.IsFunctionOf(Input)))
                body.Add(LinqExpression.Assign(
                    Declare<double>(locals, map, i.Left), 
                    i.Right.Compile(map)));

            // for (int n = 0; n < SampleCount; ++n)
            ParameterExpression n = Declare<int>(locals, "n");
            For(body,
                () => body.Add(LinqExpression.Assign(n, LinqExpression.Constant(0))),
                LinqExpression.LessThan(n, SampleCount),
                () => body.Add(LinqExpression.PreIncrementAssign(n)),
                () =>
                {
                    // Prepare input samples for oversampling interpolation.
                    Dictionary<Expression, LinqExpression> dVi = new Dictionary<Expression, LinqExpression>();
                    foreach (Expression i in Input)
                    {
                        // Ensure that we have a global variable to store the previous sample in.
                        LinqExpression Va = globals[i];
                        LinqExpression Vb = LinqExpression.MakeIndex(
                            buffers[i],
                            buffers[i].Type.GetProperty("Item"),
                            new LinqExpression[] { n });

                        // double Vi = Va
                        body.Add(LinqExpression.Assign(
                            Declare<double>(locals, map, i, i.ToString()),
                            Va));

                        // dVi = (Vb - Vi) / Oversample.
                        body.Add(LinqExpression.Assign(
                            Declare<double>(locals, dVi, i, "d" + i.ToString().Replace("[t]", "")),
                            LinqExpression.Multiply(LinqExpression.Subtract(Vb, Va), invOversample)));

                        // Va = Vb
                        body.Add(LinqExpression.Assign(Va, Vb));
                    }

                    // Prepare output sample accumulators for low pass filtering.
                    Dictionary<Expression, LinqExpression> Vo = new Dictionary<Expression, LinqExpression>();
                    foreach (Expression i in Output)
                        body.Add(LinqExpression.Assign(
                            Declare<double>(locals, Vo, i, i.ToString().Replace("[t]", "")),
                            LinqExpression.Constant(0.0)));

                    // int ov = Oversample; 
                    // do { -- ov; } while(ov > 0)
                    ParameterExpression ov = Declare<int>(locals, "ov");
                    body.Add(LinqExpression.Assign(ov, Oversample));
                    DoWhile(body,
                        () =>
                        {
                            // t += h
                            body.Add(LinqExpression.AddAssign(t, h));

                            // Interpolate the input samples.
                            foreach (Expression i in Input)
                                body.Add(LinqExpression.AddAssign(map[i], dVi[i]));

                            // Compile the trivial timestep expressions that are a function of the input.
                            foreach (Arrow i in trivial.Where(i => IsExpressionUsed(Output, i.Left) && i.Right.IsFunctionOf(Input)))
                                body.Add(LinqExpression.Assign(
                                    Declare<double>(locals, map, i.Left),
                                    i.Right.Compile(map)));

                            // We have to compute all of the Vt expressions before updating the global, so store them here.
                            Dictionary<Expression, LinqExpression> Vt = new Dictionary<Expression, LinqExpression>();
                            // Compile the differential timestep expressions.
                            foreach (Arrow i in differential.Where(i => IsExpressionUsed(Output, i.Left)))
                                Vt[i.Left] = Declare(locals, body, i.Left.ToString(), i.Right.Compile(map));
                            // Update differentials.
                            foreach (Arrow i in differential.Where(i => IsExpressionUsed(Output, i.Left)))
                            {
                                Expression di_dt = D(i.Left, Simulation.t);
                                LinqExpression Vt0 = globals[i.Left.Evaluate(t_t0)];

                                // double dV = (Vt - Vt0) / h, but we already divided by h when solving the system.
                                if (IsExpressionUsed(Output, di_dt))
                                    body.Add(LinqExpression.Assign(
                                        Declare<double>(locals, map, di_dt, "d" + i.Left.ToString().Replace("[t]", "")), 
                                        LinqExpression.Subtract(Vt[i.Left], Vt0)));

                                // Vt0 = Vt
                                body.Add(LinqExpression.Assign(Vt0, Vt[i.Left]));
                                map[i.Left] = Vt[i.Left];
                            }

                            // And the linear timestep expressions.
                            foreach (Arrow i in linear.Where(i => IsExpressionUsed(Output, i.Left)))
                                body.Add(LinqExpression.Assign(
                                    Declare<double>(locals, map, i.Left), 
                                    i.Right.Compile(map)));

                            // Solve the remaining non-linear system with Newton's method.
                            if (unknowns.Any())
                            {
                                // int it = Oversample
                                // do { ... --it } while(it > 0)
                                ParameterExpression it = Declare<int>(locals, "it");
                                body.Add(LinqExpression.Assign(it, Iterations));
                                DoWhile(body,
                                    () =>
                                    {
                                        // Compute one iteration of Newton's method.
                                        List<Arrow> iter = nonlinear.NSolve(unknowns.Select(i => Arrow.New(i, i.Evaluate(t_t0))), 1);

                                        foreach (Arrow i in iter)
                                        {
                                            LinqExpression Vt0 = globals[i.Left.Evaluate(t_t0)];
                                            body.Add(LinqExpression.Assign(Vt0, i.Right.Compile(map)));
                                            map[i.Left] = Vt0;
                                        }

                                        // --it;
                                        body.Add(LinqExpression.PreDecrementAssign(it));
                                    },
                                    LinqExpression.GreaterThan(it, LinqExpression.Constant(0)));

                                // Update f0.
                                foreach (Arrow i in f0)
                                    body.Add(LinqExpression.Assign(globals[i.Left], i.Right.Compile(map)));
                            }

                            // Compile the component voltage expressions.
                            foreach (Arrow i in components.Where(i => IsExpressionUsed(Output, i.Left)))
                                body.Add(LinqExpression.Assign(
                                    Declare<double>(locals, map, i.Left),
                                    i.Right.Compile(map)));

                            // t0 = t
                            body.Add(LinqExpression.Assign(t0, t));

                            // Vo += i.Evaluate()
                            foreach (Expression i in Output)
                                body.Add(LinqExpression.AddAssign(Vo[i], i.Compile(map)));

                            // Vi_t0 = Vi
                            foreach (Expression i in Input)
                                body.Add(LinqExpression.Assign(map[i.Evaluate(t_t0)], map[i]));

                            // --ov;
                            body.Add(LinqExpression.PreDecrementAssign(ov));
                        },
                        LinqExpression.GreaterThan(ov, LinqExpression.Constant(0)));

                    // Output[i][n] = Vo / Oversample
                    foreach (Expression i in Output)
                        body.Add(LinqExpression.Assign(
                            LinqExpression.MakeIndex(buffers[i], buffers[i].Type.GetProperty("Item"), new LinqExpression[] { n }),
                            LinqExpression.Multiply(Vo[i], invOversample)));
                });

            // return t
            LinqExpressions.LabelTarget returnTo = LinqExpression.Label(t.Type);
            body.Add(LinqExpression.Return(returnTo, t, t.Type));
            body.Add(LinqExpression.Label(returnTo, t));

            // Put it all together.
            return LinqExpression.Lambda(LinqExpression.Block(locals, body), parameters);
        }

        // Generate a for loop given the header and body expressions.
        private void For(
            IList<LinqExpression> Target,
            Action Init,
            LinqExpression Condition,
            Action Step,
            Action<LinqExpressions.LabelTarget, LinqExpressions.LabelTarget> Body)
        {
            string name = Target.Count.ToString();
            LinqExpressions.LabelTarget begin = LinqExpression.Label("for_" + name + "_begin");
            LinqExpressions.LabelTarget end = LinqExpression.Label("for_" + name + "_end");

            // Generate the init code.
            Init();

            // Check the condition, exit if necessary.
            Target.Add(LinqExpression.Label(begin));
            Target.Add(LinqExpression.IfThen(LinqExpression.Not(Condition), LinqExpression.Goto(end)));

            // Generate the body code.
            Body(end, begin);

            // Generate the step code.
            Step();
            Target.Add(LinqExpression.Goto(begin));

            // Exit point.
            Target.Add(LinqExpression.Label(end));
        }

        // Generate a for loop given the header and body expressions.
        private void For(
            IList<LinqExpression> Target,
            Action Init,
            LinqExpression Condition,
            Action Step,
            Action Body)
        {
            For(Target, Init, Condition, Step, (x, y) => Body());
        }

        // Generate a while loop given the condition and body expressions.
        private void While(
            IList<LinqExpression> Target,
            LinqExpression Condition,
            Action<LinqExpressions.LabelTarget, LinqExpressions.LabelTarget> Body)
        {
            string name = (Target.Count + 1).ToString();
            LinqExpressions.LabelTarget begin = LinqExpression.Label("while_" + name + "_begin");
            LinqExpressions.LabelTarget end = LinqExpression.Label("while_" + name + "_end");

            // Check the condition, exit if necessary.
            Target.Add(LinqExpression.Label(begin));
            Target.Add(LinqExpression.IfThen(LinqExpression.Not(Condition), LinqExpression.Goto(end)));

            // Generate the body code.
            Body(end, begin);

            // Loop.
            Target.Add(LinqExpression.Goto(begin));

            // Exit label.
            Target.Add(LinqExpression.Label(end));
        }

        // Generate a while loop given the condition and body expressions.
        private void While(
            IList<LinqExpression> Target,
            LinqExpression Condition,
            Action Body)
        {
            While(Target, Condition, (x, y) => Body());
        }

        // Generate a do-while loop given the condition and body expressions.
        private void DoWhile(
            IList<LinqExpression> Target,
            Action<LinqExpressions.LabelTarget, LinqExpressions.LabelTarget> Body,
            LinqExpression Condition)
        {
            string name = (Target.Count + 1).ToString();
            LinqExpressions.LabelTarget begin = LinqExpression.Label("do_" + name + "_begin");
            LinqExpressions.LabelTarget end = LinqExpression.Label("do_" + name + "_end");

            // Check the condition, exit if necessary.
            Target.Add(LinqExpression.Label(begin));

            // Generate the body code.
            Body(end, begin);

            // Loop.
            Target.Add(LinqExpression.IfThen(Condition, LinqExpression.Goto(begin)));

            // Exit label.
            Target.Add(LinqExpression.Label(end));
        }

        // Generate a do-while loop given the condition and body expressions.
        private void DoWhile(
            IList<LinqExpression> Target,
            Action Body,
            LinqExpression Condition)
        {
            DoWhile(Target, (x, y) => Body(), Condition);
        }

        private static ParameterExpression Declare<T>(IList<ParameterExpression> Scope, IDictionary<Expression, LinqExpression> Map, Expression Expr, string Name)
        {
            ParameterExpression p = LinqExpression.Parameter(typeof(T), Name);
            Scope.Add(p);
            if (Map != null)
                Map.Add(Expr, p);
            return p;
        }

        private static ParameterExpression Declare<T>(IList<ParameterExpression> Scope, IDictionary<Expression, LinqExpression> Map, Expression Expr)
        {
            return Declare<T>(Scope, Map, Expr, Expr.ToString());
        }

        private static ParameterExpression Declare<T>(IList<ParameterExpression> Scope, string Name)
        {
            return Declare<T>(Scope, null, null, Name);
        }

        private static ParameterExpression Declare(IList<ParameterExpression> Scope, IList<LinqExpression> Target, string Name, LinqExpression Init)
        {
            ParameterExpression p = LinqExpression.Parameter(Init.Type, Name);
            Scope.Add(p);
            Target.Add(LinqExpression.Assign(p, Init));
            return p;
        }

        // Logging helpers.
        private void LogExpressions(string Title, IEnumerable<Expression> Expressions)
        {
            log.WriteLine(MessageType.Info, Title);
            foreach (Expression i in Expressions)
                log.WriteLine(MessageType.Info, "  " + i.ToString());
        }

        private System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        private void LogTime(MessageType Type, string Message, bool Reset)
        {
            if (Reset)
            {
                watch.Reset();
                watch.Start();
            }
            log.WriteLine(Type, "[" + watch.ElapsedMilliseconds + " ms] " + Message);
        }
        private void LogTime(MessageType Type, string Message) { LogTime(Type, Message, false); }

        // Shorthand for df/dx.
        private static Expression D(Expression f, Expression x) { return Call.D(f, x); }

        // Check if x is a derivative
        private static bool IsD(Expression f, Expression x)
        {
            Call C = f as Call;
            if (!ReferenceEquals(C, null))
                return C.Target.Name == "D" && C.Arguments[1].Equals(x);
            return false;
        }

        // Get the expression that x is a derivative of.
        private static Expression DOf(Expression x)
        {
            Call d = (Call)x;
            if (d.Target.Name == "D")
                return d.Arguments.First();
            throw new InvalidOperationException("Expression is not a derivative");
        }

        // Returns 1 / x.
        private static LinqExpression Reciprocal(LinqExpression x)
        {
            LinqExpression one = null;
            if (x.Type == typeof(double))
                one = LinqExpression.Constant(1.0);
            else if (x.Type == typeof(float))
                one = LinqExpression.Constant(1.0f);
            return LinqExpression.Divide(one, x);
        }

        // Test if f is a linear function of x.
        private static bool IsLinearFunctionOf(Expression f, IEnumerable<Expression> x)
        {
            foreach (Expression i in x)
            {
                // TODO: There must be a more efficient way to do this...
                Expression fi = f / i;
                if (!fi.IsFunctionOf(i))
                    return true;

                //if (Add.TermsOf(f).Count(j => Multiply.TermsOf(j).Sum(k => k.Equals(i) ? 1 : k.IsFunctionOf(i) ? 2 : 0) == 1) == 1)
                //    return true;
            }
            return false;
        }

        private static IEnumerable<T> Concat<T>(IEnumerable<T> x0, params IEnumerable<T>[] x)
        {
            IEnumerable<T> concat = x0;
            foreach (IEnumerable<T> i in x)
                concat = concat.Concat(i);
            return concat;
        }
    }
}
