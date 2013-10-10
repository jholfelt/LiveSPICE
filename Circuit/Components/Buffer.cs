﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SyMath;
using System.ComponentModel;

namespace Circuit
{
    /// <summary>
    /// Buffer is an ideal voltage follower, i.e. it has infinite input impedance and zero output impedance.
    /// </summary>
    [CategoryAttribute("Standard")]
    [DisplayName("Buffer")]
    public class Buffer : TwoTerminal
    {
        public override void Analyze(IList<Equal> Mna, IList<Expression> Unknowns)
        {
            // Infinite input impedance.
            Anode.i = Constant.Zero;

            // Unknown output current.
            Cathode.i = Call.New(ExprFunction.New("i" + Name, t), t);
            Unknowns.Add(Cathode.i);

            Mna.Add(Equal.New(Anode.V, Cathode.V));
        }

        protected override void DrawSymbol(SymbolLayout Sym)
        {
            Sym.AddWire(Anode, new Coord(0, 10));
            Sym.AddWire(Cathode, new Coord(0, -10));

            Sym.AddLoop(EdgeType.Black,
                new Coord(-10, 10),
                new Coord(10, 10),
                new Coord(0, -10));
        }
    }
}
